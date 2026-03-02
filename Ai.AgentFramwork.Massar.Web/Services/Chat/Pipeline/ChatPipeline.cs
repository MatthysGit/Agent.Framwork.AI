using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Tools;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;

/// <summary>
/// Orchestrates a single chat turn:
/// 1) Applies policy gates (e.g., individual compensation).
/// 2) Routes the request to the correct agent/mode (doc search/edit, SQL, chart, or general).
/// 3) Executes the chosen path and shapes output into the UI contract (JSON for doc tools, markdown for others).
///
/// NOTE:
/// - DocSearch/DocEdit paths intentionally return JSON unchanged (attachments contract).
/// - SQL paths are forced to execute without clarification questions.
/// - Chart mode uses SQL -> strict JSON -> chart image; with table fallback.
/// </summary>
public sealed class ChatPipeline
{
    // ----------------------------
    // Dependencies (injected)
    // ----------------------------

    /// <summary>
    /// Router determines which agent/mode to use (e.g., chart vs. normal, SQL agent vs. general).
    /// Typically implemented via an LLM prompt that emits a structured decision.
    /// </summary>
    private readonly RouterAgent _router;

    /// <summary>
    /// Tool that calls specialist agents by name and returns their final response.
    /// </summary>
    private readonly AgentCallerTool _caller;

    /// <summary>
    /// Chart rendering tools (bar/pie/line/etc.) that produce PNGs and return a URL.
    /// </summary>
    private readonly ChatTools _chartTools;

    /// <summary>
    /// Document search tool; returns JSON in your attachment schema.
    /// </summary>
    private readonly DocumentSearchTool _docSearchTool;

    /// <summary>
    /// Document edit tool; returns JSON in your attachment schema.
    /// </summary>
    private readonly DocumentEditTool _docEditTool;

    /// <summary>
    /// Retrieves the current conversation ID used by doc tools.
    /// </summary>
    private readonly Func<Guid> _getConversationId;

    /// <summary>
    /// Authorization gate: whether the current user can see individual compensation.
    /// (Aggregated compensation is usually allowed; individual is blocked unless authorized.)
    /// </summary>
    private readonly Func<Task<bool>> _canViewCompensationAsync;

    /// <summary>
    /// Authorization gate for privileged operations (your comment indicates "RoleId == 3",
    /// but you later moved it to DB-driven PrivilegedPPI. This delegate abstracts that detail).
    /// </summary>
    private readonly Func<Task<bool>> _isPrivilegedAsync; // RoleId == 3

    public ChatPipeline(
        RouterAgent router,
        AgentCallerTool caller,
        ChatTools chartTools,
        DocumentSearchTool docSearchTool,
        DocumentEditTool docEditTool,
        Func<Guid> getConversationId,
        Func<Task<bool>> canViewCompensationAsync,
        Func<Task<bool>> isPrivilegedAsync)
    {
        _router = router;
        _caller = caller;
        _chartTools = chartTools;
        _docSearchTool = docSearchTool;
        _docEditTool = docEditTool;
        _getConversationId = getConversationId;
        _canViewCompensationAsync = canViewCompensationAsync;
        _isPrivilegedAsync = isPrivilegedAsync;
    }

    /// <summary>
    /// Final output returned to the chat service/UI.
    /// - Text: final assistant content (markdown, JSON, etc. depending on path)
    /// - RoutedAgent: the agent name chosen/executed (or "PolicyGuard")
    /// - RouterReason: explanation from the router (or policy reason)
    /// </summary>
    public sealed record PipelineResult(string Text, string RoutedAgent, string RouterReason);

    /// <summary>
    /// Executes one user turn end-to-end.
    /// IMPORTANT ORDER:
    /// 1) Policy gate(s) (must happen before routing/tools)
    /// 2) Router decision
    /// 3) Execute chosen path (doc, chart, sql, default agent)
    /// 4) Optional PPI pass (currently stubbed to no-op)
    /// </summary>
    public async Task<PipelineResult> ExecuteAsync(string userText, CancellationToken ct = default)
    {
        // --------------------------------------------------------------------
        // 🔒 Sensitive compensation gate (MUST run BEFORE routing/tools)
        //
        // This prevents leakage even if the router misroutes or tools respond oddly.
        // --------------------------------------------------------------------
        if (IsIndividualCompensationQuestion(userText))
        {
            var allowed = await _canViewCompensationAsync();
            if (!allowed)
            {
                return new PipelineResult(
                    "I can’t help with an individual’s salary/compensation. If you’re HR/Payroll authorized, please use the approved HR workflow. " +
                    "I can help with aggregated totals or salary ranges instead (e.g., by department/role).",
                    RoutedAgent: "PolicyGuard",
                    RouterReason: "Blocked: individual compensation request");
            }
        }

        // Total time for this pipeline execution (for diagnostics)
        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        // Time spent in routing only
        var swRoute = System.Diagnostics.Stopwatch.StartNew();

        // Router decides:
        // - r.Agent (which specialist)
        // - r.Mode (e.g., "chart" vs "normal")
        // - r.ChartType (if chart mode)
        // - r.Reason (explain why)
        var r = await _router.RouteAsync(userText, ct);

        swRoute.Stop();
        Console.WriteLine(
            $"[ROUTER] mode={r.Mode} agent={r.Agent} chartType={r.ChartType ?? "null"} " +
            $"reason=\"{r.Reason}\" routeMs={swRoute.ElapsedMilliseconds}");

        // --------------------------------------------------------------------
        // Document search/edit are executed directly via tools so the JSON stays
        // identical to what your attachment/UI expects.
        // --------------------------------------------------------------------
        if (r.Agent.Equals(ChatAgentFactory.DocumentSearchAgentName, StringComparison.OrdinalIgnoreCase))
        {
            var convoId = _getConversationId();
            var json = await _docSearchTool.SearchDocumentsAsync(convoId, userText);

            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={r.Agent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return new PipelineResult(json ?? "No relevant information found.", r.Agent, r.Reason);
        }

        if (r.Agent.Equals(ChatAgentFactory.DocumentEditAgentName, StringComparison.OrdinalIgnoreCase))
        {
            var convoId = _getConversationId();

            // Infer a stable “edit instruction” label that the edit tool can use
            // (keeps user input + intent separate).
            var instruction = InferEditInstruction(userText);

            var json = await _docEditTool.EditDocumentAsync(convoId, userText, instruction);

            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={r.Agent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return new PipelineResult(json ?? "No edit result returned.", r.Agent, r.Reason);
        }

        // --------------------------------------------------------------------
        // Chart path:
        // - Force SQL agent to return STRICT chart JSON
        // - Parse/normalize chart JSON (supports multiple schemas)
        // - Render chart -> return markdown image
        // - If chart fails, fallback to SQL table JSON -> markdown table
        // --------------------------------------------------------------------
        if (r.Mode.Equals("chart", StringComparison.OrdinalIgnoreCase))
        {
            // Normalize chart type (your renderer wants "bar" for columns)
            var desired = string.IsNullOrWhiteSpace(r.ChartType) ? "column" : r.ChartType!.Trim().ToLowerInvariant();
            if (desired == "bar") desired = "column";

            // Contract-enforcement prompt:
            // - No follow-up questions allowed
            // - Must use your SQL tool order rules
            // - Must output ONLY strict JSON with the specified schema
            var sqlPrompt = string.Format(@"
You are the SQL agent.

The user request MUST be answered WITHOUT asking follow-up questions.
Do NOT ask for more context. Do NOT explain. Do NOT output markdown.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

You MUST return ONLY strict JSON using EXACTLY this schema:

{{
  ""chartType"": ""column"",
  ""title"": ""<short title>"",
  ""xAxis"": {{
    ""title"": ""<x axis title>"",
    ""categories"": [""A"",""B"",""C""]
  }},
  ""yAxis"": {{
    ""title"": ""<y axis title>""
  }},
  ""series"": [
    {{
      ""name"": ""<series name>"",
      ""data"": [1,2,3]
    }}
  ]
}}

Rules:
- If you can answer from the database, do so.
- If the request is ambiguous and you cannot confidently query, you MUST still return JSON.
  In that case return a safe 'No data' payload:
{{
  ""chartType"": ""column"",
  ""title"": ""No data / insufficient context"",
  ""xAxis"": {{ ""title"": """", ""categories"": [""No data""] }},
  ""yAxis"": {{ ""title"": """" }},
  ""series"": [{{ ""name"": ""No data"", ""data"": [0] }}]
}}

User request:
{0}
", userText);

            var sql = await _caller.CallAgentAsync(ChatAgentFactory.SqlAgentName, sqlPrompt, cancellationToken: ct);

            // Raw output logging is critical because chart parsing is brittle.
            Console.WriteLine("SQL_AGENT_RAW_FOR_CHART:\n" + sql.Text);

            // Extract first JSON object in case the model wrapped JSON in fences or added stray text.
            var jsonOnly = ExtractFirstJsonObject(sql.Text);
            if (string.IsNullOrWhiteSpace(jsonOnly))
            {
                Console.WriteLine("[SQL_CHART_CONTRACT_VIOLATION] Non-JSON response from SQL agent:\n" + sql.Text);

                // Safe default so chart renderer doesn't crash.
                jsonOnly = @"{
  ""chartType"": ""column"",
  ""title"": ""No data / SQL agent returned non-JSON"",
  ""xAxis"": { ""title"": """", ""categories"": [""No data""] },
  ""yAxis"": { ""title"": """" },
  ""series"": [{ ""name"": ""No data"", ""data"": [0] }]
}";
            }

            try
            {
                // Parse chart JSON and normalize schema variants into ChartPayload.
                var chart = ParseChartJson(jsonOnly);
                Console.WriteLine($"CHART_PARSED: type={chart.ChartType}, labels={(chart.Labels?.Count ?? 0)}, values={(chart.Values?.Count ?? 0)}, series={(chart.Series?.Count ?? 0)}");

                // Render and get URL to PNG
                var url = await CreateChartAsync(chart);

                swTotal.Stop();
                Console.WriteLine($"[PIPELINE] done agent={ChatAgentFactory.SqlAgentName} mode=chart totalMs={swTotal.ElapsedMilliseconds}");

                // UI contract: return markdown image only
                return new PipelineResult($"![chart]({url})", ChatAgentFactory.SqlAgentName, r.Reason);
            }
            catch (Exception ex)
            {
                // If parsing or rendering fails, we attempt a safer table-based fallback.
                Console.WriteLine("[CHART_FALLBACK] Chart failed: " + ex);

                var tablePrompt = string.Format(@"
The user asked for a chart, but chart rendering failed.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

Return ONLY JSON in this schema (no markdown, no prose):
{{
  ""title"": ""<short title>"",
  ""columns"": [""Col1"",""Col2""],
  ""rows"": [
    [""A"",""1""],
    [""B"",""2""]
  ]
}}

User request:
{0}
", userText);

                var tableJson = await _caller.CallAgentAsync(ChatAgentFactory.SqlAgentName, tablePrompt, cancellationToken: ct);

                // Extract JSON (same reason as above)
                var tableJsonOnly = ExtractFirstJsonObject(tableJson.Text) ?? tableJson.Text;

                // Convert JSON -> markdown table; if that fails, surface the error.
                var fallbackMd = TryRenderTableMarkdown(tableJsonOnly, out var tableMd)
                    ? tableMd
                    : $"Chart generation failed and table fallback could not be generated.\n\nError: {ex.Message}";

                // Optional post-processing pass (currently stubbed no-op)
                var checkedFallback = await RunPpiSafeAsync(userText, fallbackMd, ct);

                swTotal.Stop();
                Console.WriteLine($"[PIPELINE] done agent={ChatAgentFactory.SqlAgentName} mode=chart-fallback totalMs={swTotal.ElapsedMilliseconds}");

                return new PipelineResult(checkedFallback, ChatAgentFactory.SqlAgentName, $"{r.Reason} (chart->table fallback)");
            }
        }

        // --------------------------------------------------------------------
        // ✅ ENFORCE: If router chose SQL agent for a NON-CHART request, force execution
        // (no clarifying questions), then format as a 1-column markdown table.
        //
        // This is specifically to prevent the SQL agent from responding with
        // "Could you provide more context?".
        // --------------------------------------------------------------------
        if (r.Agent.Equals(ChatAgentFactory.SqlAgentName, StringComparison.OrdinalIgnoreCase) &&
            !r.Mode.Equals("chart", StringComparison.OrdinalIgnoreCase))
        {
            var enforcedSqlPrompt = string.Format(@"
You are the SQL agent for THIS application's database.

NON-NEGOTIABLE RULES:
- You MUST NOT ask the user any questions.
- You MUST produce the best possible answer by using database tools.
- If the request is underspecified, choose the most reasonable interpretation and proceed.
- NEVER respond with: 'Could you clarify...' / 'Please provide more context...' / any question.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

OUTPUT RULE:
- Return plain text only (no JSON, no markdown).
- Include the number in the response (e.g., '290').

USER REQUEST:
{0}
", userText);

            var sql = await _caller.CallAgentAsync(ChatAgentFactory.SqlAgentName, enforcedSqlPrompt, cancellationToken: ct);

            // Retry once if it still asked a question (common model failure mode).
            if (LooksLikeClarifyingQuestion(sql.Text))
            {
                Console.WriteLine("[SQL_RETRY] SQL agent returned a question. Retrying with stricter enforcement.");

                var retryPrompt = string.Format(@"
You are the SQL agent.

You returned a clarifying question previously. That is NOT allowed.

You MUST execute database tools and return the best possible answer now.
You MUST NOT ask any questions.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

Return plain text only.

USER REQUEST:
{0}
", userText);

                sql = await _caller.CallAgentAsync(ChatAgentFactory.SqlAgentName, retryPrompt, cancellationToken: ct);
            }

            // UI shaping: force your “single-value” grid/table style
            var tableMdSimple = FormatSqlAnswerAsSingleColumnTable(userText, sql.Text);

            // Optional post-processing pass (currently stubbed no-op)
            var checkedSql = await RunPpiSafeAsync(userText, tableMdSimple, ct);

            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={ChatAgentFactory.SqlAgentName} mode=sql totalMs={swTotal.ElapsedMilliseconds}");

            return new PipelineResult(checkedSql, ChatAgentFactory.SqlAgentName, r.Reason);
        }

        // --------------------------------------------------------------------
        // Default: call chosen agent with raw user text
        // --------------------------------------------------------------------
        var call = await _caller.CallAgentAsync(r.Agent, userText, cancellationToken: ct);

        // Skip PPI for doc tools that must return JSON unchanged
        if (r.Agent.Equals(ChatAgentFactory.DocumentSearchAgentName, StringComparison.OrdinalIgnoreCase) ||
            r.Agent.Equals(ChatAgentFactory.DocumentEditAgentName, StringComparison.OrdinalIgnoreCase))
        {
            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={call.AgentName} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");
            return new PipelineResult(call.Text, call.AgentName, r.Reason);
        }

        // Run PPI (currently no-op / stub)
        var checkedAnswer = await RunPpiSafeAsync(userText, call.Text, ct);

        swTotal.Stop();
        Console.WriteLine($"[PIPELINE] done agent={call.AgentName} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

        return new PipelineResult(checkedAnswer, call.AgentName, r.Reason);
    }

    /// <summary>
    /// Lightweight detector for “individual compensation” questions (block unless authorized).
    /// This is a heuristic: it may produce false positives/negatives.
    /// (You also have a richer ClassifyPolicy() below; consider consolidating.)
    /// </summary>
    private static bool IsIndividualCompensationQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.ToLowerInvariant();

        // Compensation keywords
        var comp =
            t.Contains("salary") ||
            t.Contains("compensation") ||
            t.Contains("pay") ||
            t.Contains("wage") ||
            t.Contains("bonus") ||
            t.Contains("earn");

        if (!comp) return false;

        // “individual” targeting signals
        var individual =
            t.Contains("what is ") ||
            t.Contains("what's ") ||
            t.Contains("how much does ") ||
            t.Contains("his ") ||
            t.Contains("her ") ||
            t.Contains("'s ") ||
            t.Contains("salary of ");

        return individual;
    }

    /// <summary>
    /// Intended place to run PPI checks (post-processing: safety, number inflation, policy).
    /// Currently returns draft unchanged because this method is static and has no access to _caller.
    ///
    /// Recommendation:
    /// - Make this method instance-based so it can call _caller, OR
    /// - Pass in a delegate Func&lt;string,string,Task&lt;string&gt;&gt; that invokes PPI agent.
    /// </summary>
    private static async Task<string> RunPpiSafeAsync(string userText, string draft, CancellationToken ct)
    {
        try
        {
            return draft;
        }
        catch
        {
            return draft;
        }
    }

    /// <summary>
    /// Converts a freeform user “edit request” into a stable instruction label used by DocumentEditTool.
    /// </summary>
    private static string InferEditInstruction(string userText)
    {
        var lower = (userText ?? "").ToLowerInvariant();
        if (lower.Contains("comment")) return "add comments";
        if (lower.Contains("annotate")) return "annotate with comments";
        if (lower.Contains("highlight")) return "highlight issues and add comments";
        if (lower.Contains("proofread")) return "proofread and add comments";
        if (lower.Contains("revise") || lower.Contains("edit")) return "make suggested edits and add comments";
        return "add comments";
    }

    /// <summary>
    /// Heuristic to detect whether an agent response is trying to ask the user for more context.
    /// Used to trigger a forced retry for the SQL agent.
    /// </summary>
    private static bool LooksLikeClarifyingQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant();

        if (t.Contains("clarify") || t.Contains("please specify") || t.Contains("more context"))
            return true;

        if (t.EndsWith("?") && (t.Contains("which ") || t.Contains("what ") || t.Contains("where ") || t.Contains("who ")))
            return true;

        return false;
    }

    /// <summary>
    /// Extracts the first JSON object found in a string (handles code fences / extra prose).
    /// Used for “LLM contract enforcement” when models occasionally add explanation text.
    /// </summary>
    private static string? ExtractFirstJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var s = text.Trim();

        // Remove common code fences
        s = s.Replace("```json", "", StringComparison.OrdinalIgnoreCase)
             .Replace("```", "");

        var start = s.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return s.Substring(start, i - start + 1);
            }
        }

        return null;
    }

    // Chart JSON schema support (native schema + SQL-agent schema normalization)
    private static ChartPayload ParseChartJson(string json)
    {
        // 1) Attempt direct deserialize into ChartPayload (your native payload shape).
        try
        {
            var direct = JsonSerializer.Deserialize<ChartPayload>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (direct is not null && !string.IsNullOrWhiteSpace(direct.ChartType))
            {
                // Validate single-series schema (Labels+Values)
                var hasSingle =
                    direct.Labels is { Count: > 0 } &&
                    direct.Values is { Count: > 0 } &&
                    direct.Labels.Count == direct.Values.Count;

                // Validate multi-series schema (Labels+Series[*].Values)
                var hasMulti =
                    direct.Labels is { Count: > 0 } &&
                    direct.Series is { Count: > 0 } &&
                    direct.Series.All(s => (s.Values?.Count ?? 0) == direct.Labels.Count);

                if (hasSingle || hasMulti)
                {
                    direct.ChartType = NormalizeChartType(direct.ChartType);
                    return direct;
                }
            }
        }
        catch
        {
            // fall through to schema normalization
        }

        // 2) Normalize SQL-agent schema: xAxis.categories + series.data.
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? chartTypeRaw = null;
            if (TryGetPropIgnoreCase(root, "chartType", out var ctEl) && ctEl.ValueKind == JsonValueKind.String)
                chartTypeRaw = ctEl.GetString();

            var chartType = NormalizeChartType(chartTypeRaw);

            var title = "Chart";
            if (TryGetPropIgnoreCase(root, "title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                title = titleEl.GetString() ?? "Chart";

            // Some models might output "xis" accidentally; you support both.
            JsonElement xObj = default;
            if (!(TryGetPropIgnoreCase(root, "xAxis", out xObj) || TryGetPropIgnoreCase(root, "xis", out xObj)))
                xObj = default;

            // Categories become Labels
            var labels = new List<string>();
            if (xObj.ValueKind == JsonValueKind.Object &&
                TryGetPropIgnoreCase(xObj, "categories", out var catsEl) &&
                catsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in catsEl.EnumerateArray())
                    labels.Add(c.GetString() ?? "");
            }

            // Axis titles
            var xAxisLabel = "";
            if (xObj.ValueKind == JsonValueKind.Object &&
                TryGetPropIgnoreCase(xObj, "title", out var xt) &&
                xt.ValueKind == JsonValueKind.String)
            {
                xAxisLabel = xt.GetString() ?? "";
            }

            var yAxisLabel = "";
            if (TryGetPropIgnoreCase(root, "yAxis", out var yEl) && yEl.ValueKind == JsonValueKind.Object &&
                TryGetPropIgnoreCase(yEl, "title", out var yt))
            {
                yAxisLabel = yt.ValueKind == JsonValueKind.String ? (yt.GetString() ?? "") : yt.ToString();
            }

            // Series normalization
            var seriesList = new List<ChartSeries>();
            if (TryGetPropIgnoreCase(root, "series", out var sEl) && sEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sEl.EnumerateArray())
                {
                    var name = "Series";
                    if (TryGetPropIgnoreCase(s, "name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                        name = nEl.GetString() ?? "Series";

                    JsonElement dataEl = default;
                    if (!(TryGetPropIgnoreCase(s, "data", out dataEl) || TryGetPropIgnoreCase(s, "values", out dataEl)))
                        dataEl = default;

                    var vals = new List<double>();
                    if (dataEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in dataEl.EnumerateArray())
                        {
                            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
                                vals.Add(d);
                            else if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var ds))
                                vals.Add(ds);
                        }
                    }

                    seriesList.Add(new ChartSeries { Name = name, Values = vals });
                }
            }

            // If multiple series, force multicolumn (your renderer uses that)
            if (seriesList.Count > 1)
            {
                return new ChartPayload
                {
                    ChartType = "multicolumn",
                    Title = title,
                    XAxisLabel = xAxisLabel,
                    YAxisLabel = yAxisLabel,
                    Labels = labels,
                    Series = seriesList
                };
            }

            // Single series -> use Values
            var single = seriesList.FirstOrDefault();
            return new ChartPayload
            {
                ChartType = chartType,
                Title = title,
                XAxisLabel = xAxisLabel,
                YAxisLabel = yAxisLabel,
                Labels = labels,
                Values = single?.Values ?? new List<double>()
            };
        }
        catch
        {
            // Hard fallback ensures chart rendering never blows up the UI.
            return new ChartPayload
            {
                ChartType = "bar",
                Title = "Chart",
                Labels = new List<string> { "No data" },
                Values = new List<double> { 0 }
            };
        }
    }

    /// <summary>
    /// Case-insensitive JSON property fetch helper.
    /// </summary>
    private static bool TryGetPropIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Normalizes chart type tokens to the set your ChatTools understands.
    /// </summary>
    private static string NormalizeChartType(string? t)
    {
        var s = (t ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "column" => "bar",     // your chart tool uses bar to represent column charts
            "columns" => "bar",
            "bar" => "bar",
            "pie" => "pie",
            "line" => "line",
            "area" => "area",
            "donut" => "donut",
            "gauge" => "gauge",
            "progress" => "progress",
            "multicolumn" => "multicolumn",
            _ => "bar"
        };
    }

    /// <summary>
    /// Validates that labels and values are consistent and finite.
    /// Used to fail fast with a useful exception before chart rendering.
    /// </summary>
    private static void EnsureValidSeries(string[] labels, double[] values, string seriesName)
    {
        if (labels is null) throw new ArgumentNullException(nameof(labels));
        if (values is null) throw new ArgumentNullException(nameof(values));
        if (labels.Length == 0) throw new ArgumentException($"{seriesName}: No data to render.");
        if (labels.Length != values.Length) throw new ArgumentException($"{seriesName}: labels and values must have the same length.");

        for (int i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (double.IsNaN(v) || double.IsInfinity(v))
                throw new ArgumentException($"{seriesName}: value at index {i} is not a finite number.");
        }
    }

    /// <summary>
    /// Renders a chart based on ChartPayload. Returns a URL to a PNG.
    /// </summary>
    private Task<string> CreateChartAsync(ChartPayload p)
    {
        var t = (p.ChartType ?? "").Trim().ToLowerInvariant();

        var labels = (p.Labels ?? new List<string>()).ToArray();
        var values = (p.Values ?? new List<double>()).ToArray();

        // Validate non-multi charts (multi validates per-series)
        if (!t.Equals("multicolumn", StringComparison.OrdinalIgnoreCase) &&
            !t.Equals("gauge", StringComparison.OrdinalIgnoreCase))
        {
            EnsureValidSeries(labels, values, $"{Cap(t)} chart");
        }

        return t switch
        {
            "bar" => _chartTools.CreateBarChartPngAsync(
                p.Title ?? "Chart",
                p.XAxisLabel ?? "",
                p.YAxisLabel ?? "",
                labels,
                values),

            "pie" => _chartTools.CreatePieChartPngAsync(
                p.Title ?? "Chart",
                labels,
                values),

            "line" => _chartTools.CreateLineChartPngAsync(
                p.Title ?? "Chart",
                p.XAxisLabel ?? "",
                p.YAxisLabel ?? "",
                labels,
                values),

            "area" => _chartTools.CreateAreaChartPngAsync(
                p.Title ?? "Chart",
                p.XAxisLabel ?? "",
                p.YAxisLabel ?? "",
                labels,
                values),

            "donut" => _chartTools.CreateDonutChartPngAsync(
                p.Title ?? "Chart",
                labels,
                values),

            "gauge" => _chartTools.CreateGaugeChartPngAsync(
                p.Title ?? "Gauge",
                p.Value ?? 0,
                p.Min ?? 0,
                p.Max ?? 100),

            "progress" => _chartTools.CreateProgressBarsChartPngAsync(
                p.Title ?? "Progress",
                labels,
                values),

            "multicolumn" => CreateMultiColumnAsync(p, labels),

            _ => _chartTools.CreateBarChartPngAsync(
                p.Title ?? "Chart",
                p.XAxisLabel ?? "",
                p.YAxisLabel ?? "",
                labels,
                values),
        };
    }

    /// <summary>
    /// Multi-series column chart: validates each series and then delegates to chart tools.
    /// </summary>
    private Task<string> CreateMultiColumnAsync(ChartPayload p, string[] labels)
    {
        var series = p.Series ?? new List<ChartSeries>();

        if (labels.Length == 0)
            throw new ArgumentException("Multi-column chart: No labels/categories to render.");

        if (series.Count == 0)
            throw new ArgumentException("Multi-column chart: No series to render.");

        var seriesNames = series.Select(s => s.Name ?? "Series").ToArray();
        var seriesValues = series.Select(s => (s.Values ?? new List<double>()).ToArray()).ToArray();

        for (int i = 0; i < seriesValues.Length; i++)
            EnsureValidSeries(labels, seriesValues[i], $"Multi-column {seriesNames[i]}");

        return _chartTools.CreateMultiSeriesColumnChartPngAsync(
            p.Title ?? "Chart",
            p.XAxisLabel ?? "",
            p.YAxisLabel ?? "",
            labels,
            seriesNames,
            seriesValues,
            width: 900,
            height: 500);
    }

    /// <summary>
    /// Converts a table JSON schema into markdown.
    /// Expected schema:
    /// { "title": "...", "columns": ["A","B"], "rows": [ ["x","y"], ... ] }
    /// </summary>
    private static bool TryRenderTableMarkdown(string json, out string markdown)
    {
        markdown = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? title = null;
            if (TryGetPropIgnoreCase(root, "title", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                title = tEl.GetString();

            if (!TryGetPropIgnoreCase(root, "columns", out var colsEl) || colsEl.ValueKind != JsonValueKind.Array)
                return false;

            if (!TryGetPropIgnoreCase(root, "rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                return false;

            var cols = colsEl.EnumerateArray().Select(c => c.GetString() ?? c.ToString()).ToArray();
            if (cols.Length == 0) return false;

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title))
                sb.AppendLine($"**{title}**\n");

            sb.AppendLine("| " + string.Join(" | ", cols) + " |");
            sb.AppendLine("| " + string.Join(" | ", cols.Select(_ => "---")) + " |");

            foreach (var rowEl in rowsEl.EnumerateArray())
            {
                if (rowEl.ValueKind != JsonValueKind.Array) continue;
                var row = rowEl.EnumerateArray().Select(c => c.GetString() ?? c.ToString()).ToArray();

                var cells = row.Length < cols.Length
                    ? row.Concat(Enumerable.Repeat("", cols.Length - row.Length)).ToArray()
                    : row.Take(cols.Length).ToArray();

                sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            }

            markdown = sb.ToString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Capitalizes the first character (UI helper).
    /// </summary>
    private static string Cap(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        if (s.Length == 1) return s.ToUpperInvariant();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    // =========================
    // ✅ Simple 1-column table (matches your screenshot)
    // =========================

    /// <summary>
    /// Formats SQL result into a simple markdown table:
    /// | Header |
    /// |---:|
    /// | 123 |
    ///
    /// Prefers the first integer found anywhere in sqlText.
    /// </summary>
    private static string FormatSqlAnswerAsSingleColumnTable(string userText, string sqlText)
    {
        var text = (sqlText ?? "").Trim();
        var header = GuessHeader(userText);

        if (TryExtractFirstInteger(text, out var n))
        {
            return
                $"| {EscapeMd(header)} |\n" +
                "|---:|\n" +
                $"| {n} |\n";
        }

        if (string.IsNullOrWhiteSpace(text))
            text = "No data";

        return
            $"| {EscapeMd(header)} |\n" +
            "|---|\n" +
            $"| {EscapeMd(text).Replace("\n", "<br/>")} |\n";
    }

    /// <summary>
    /// Best-effort label for the table header based on the question.
    /// </summary>
    private static string GuessHeader(string userText)
    {
        var text = (userText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "Result";

        var lower = Regex.Replace(text.ToLowerInvariant(), @"\s+", " ");

        // 1) Strong patterns first (non-greedy capture up to common trailing question phrases)
        // Examples:
        // "how many employees do we have" -> employees
        // "how many open tickets are there" -> open tickets
        // "how many employees in dubai" -> employees (then "in dubai" can be handled elsewhere if you want)
        var strong = new[]
        {
            @"\bhow many\s+(?<thing>.+?)(?:\s+(?:do|does)\s+\w+\s+have|\s+are\s+there|\s+is\s+there|\s+exist|\s+currently|\s+today|\s+right now|\?|$)",
            @"\bnumber of\s+(?<thing>.+?)(?:\s+(?:do|does)\s+\w+\s+have|\s+are\s+there|\s+is\s+there|\?|$)",
            @"\bcount(?:\s+of)?\s+(?<thing>.+?)(?:\s+(?:do|does)\s+\w+\s+have|\s+are\s+there|\s+is\s+there|\?|$)",
            @"\btotal\s+(?<thing>.+?)(?:\s+(?:do|does)\s+\w+\s+have|\s+are\s+there|\s+is\s+there|\?|$)",
        };

        foreach (var pat in strong)
        {
            var m = Regex.Match(lower, pat, RegexOptions.IgnoreCase);
            if (!m.Success) continue;

            var thing = CleanupThing(m.Groups["thing"].Value);
            if (!string.IsNullOrWhiteSpace(thing))
                return $"{ToTitleCaseSafe(thing)} Count";
        }

        // 2) Fallback: keyword mapping (safe default)
        if (lower.Contains("employee")) return "Employee Count";
        if (lower.Contains("customer")) return "Customer Count";
        if (lower.Contains("order")) return "Order Count";
        if (lower.Contains("product")) return "Product Count";

        return "Result";
    }

    private static string CleanupThing(string thing)
    {
        thing = (thing ?? "").Trim();

        // Remove trailing punctuation
        thing = thing.TrimEnd('?', '.', '!', ',', ';', ':');

        // If the capture still contains helper phrases, cut them off.
        // e.g. "employees do we have" -> "employees"
        // e.g. "orders are there" -> "orders"
        thing = Regex.Replace(
            thing,
            @"\b(do|does)\s+\w+\s+have\b.*$|\bare\s+there\b.*$|\bis\s+there\b.*$",
            "",
            RegexOptions.IgnoreCase).Trim();

        // Cut off at common "query modifier" words (you can tune this list)
        // This keeps headers clean:
        // "employees in dubai" -> "employees"
        // "orders by month" -> "orders"
        thing = Regex.Replace(
            thing,
            @"\b(in|for|by|per|with|where|that|who|which|from|between|during|since)\b.*$",
            "",
            RegexOptions.IgnoreCase).Trim();

        // If it’s too long, cap it to avoid ugly headers
        var words = thing.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 6)
            thing = string.Join(' ', words.Take(6));

        return thing.Trim();
    }


    private static string ToTitleCaseSafe(string input)
    {
        input = (input ?? "").Trim();
        if (input.Length == 0) return input;

        var lowerWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "of", "the", "a", "an", "and", "or", "to", "in", "for", "by", "per", "with" };

        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            var w = parts[i].Trim();
            if (w.Length == 0) continue;

            if (i != 0 && lowerWords.Contains(w))
            {
                parts[i] = w.ToLowerInvariant();
                continue;
            }

            parts[i] = w.Length == 1
                ? w.ToUpperInvariant()
                : char.ToUpperInvariant(w[0]) + w[1..];
        }

        return string.Join(' ', parts);
    }
    
    /// <summary>
    /// Extracts the first integer from a string (e.g., "There are 1,234 employees").
    /// </summary>
    private static bool TryExtractFirstInteger(string text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var cleaned = text.Replace(",", "");
        var m = System.Text.RegularExpressions.Regex.Match(cleaned, @"(\d+)");
        if (!m.Success) return false;

        return long.TryParse(m.Groups[1].Value, out value);
    }

    /// <summary>
    /// Escapes markdown table-breaking characters.
    /// </summary>
    private static string EscapeMd(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("|", "\\|").Trim();
    }

    /// <summary>
    /// Internal normalized chart payload used by CreateChartAsync.
    /// Supports single-series (Labels+Values) and multi-series (Labels+Series).
    /// </summary>
    public sealed class ChartPayload
    {
        public string? ChartType { get; set; }
        public string? Title { get; set; }
        public string? XAxisLabel { get; set; }
        public string? YAxisLabel { get; set; }

        public List<string>? Labels { get; set; }
        public List<double>? Values { get; set; }

        public List<ChartSeries>? Series { get; set; }

        // gauge
        public double? Value { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
    }

    /// <summary>
    /// Multi-series chart series definition.
    /// </summary>
    public sealed class ChartSeries
    {
        public string? Name { get; set; }
        public List<double>? Values { get; set; }
    }

    // --------------------------------------------------------------------
    // Policy classification (currently unused by ExecuteAsync in your snippet)
    // --------------------------------------------------------------------

    private sealed record PolicyDecision(
        bool IsSensitive,
        bool IsAggregated,
        string Type,
        string Reason);

    /// <summary>
    /// Richer policy classifier than IsIndividualCompensationQuestion.
    /// You can unify these so you have ONE source of truth for gating.
    /// </summary>
    private static PolicyDecision ClassifyPolicy(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new PolicyDecision(false, false, "None", "Empty");

        var t = text.Trim().ToLowerInvariant();

        var aggregated =
            t.Contains("average") || t.Contains("avg") ||
            t.Contains("median") ||
            t.Contains("total") || t.Contains("sum") ||
            t.Contains("count") || t.Contains("how many") ||
            t.Contains("by department") || t.Contains("per department") ||
            t.Contains("by role") || t.Contains("by title") ||
            t.Contains("by region") || t.Contains("by office") ||
            t.Contains("range") || t.Contains("min") || t.Contains("max") ||
            t.Contains("group by") ||
            t.Contains("distribution") ||
            t.Contains("overall") && (t.Contains("salary") || t.Contains("pay") || t.Contains("compensation"));

        var asksSalary =
            t.Contains("salary") || t.Contains("compensation") || t.Contains("pay") || t.Contains("wage") || t.Contains("bonus") || t.Contains("earn");

        var individual =
            t.Contains("what is ") || t.Contains("what's ") || t.Contains("how much does ") ||
            t.Contains("his ") || t.Contains("her ") || t.Contains("their ") ||
            t.Contains("'s ") || t.Contains("salary of ") || t.Contains("pay of ") || t.Contains("compensation of ");

        var asksBank =
            t.Contains("bank") || t.Contains("account number") || t.Contains("iban") || t.Contains("swift") ||
            t.Contains("routing number") || t.Contains("sort code");

        var asksId =
            t.Contains("id number") || t.Contains("national id") || t.Contains("passport") ||
            t.Contains("emirates id") || t.Contains("ssn") || t.Contains("social security") ||
            t.Contains("driver") && t.Contains("license");

        var asksAddress =
            t.Contains("address") || t.Contains("home address") || t.Contains("residential address") ||
            t.Contains("street") || t.Contains("zip") || t.Contains("postcode") || t.Contains("po box");

        var asksPhoneEmail =
            t.Contains("phone") || t.Contains("mobile") || t.Contains("email") || t.Contains("personal email");

        if (aggregated && (asksSalary || asksBank || asksId || asksAddress || asksPhoneEmail))
        {
            if (t.Contains("list") || t.Contains("show all") || t.Contains("export") || t.Contains("download"))
            {
                return new PolicyDecision(true, false, "SensitiveBulkPII", "Bulk PII export/list request");
            }

            return new PolicyDecision(true, true, "AggregatedSensitive", "Aggregated request allowed");
        }

        if (asksSalary && individual && !aggregated)
            return new PolicyDecision(true, false, "IndividualCompensation", "Individual salary/compensation requested");

        if ((asksBank || asksId || asksAddress || asksPhoneEmail) && !aggregated)
            return new PolicyDecision(true, false, "PersonalData", "Personal data requested");

        return new PolicyDecision(false, aggregated, "None", "Not sensitive");
    }
}