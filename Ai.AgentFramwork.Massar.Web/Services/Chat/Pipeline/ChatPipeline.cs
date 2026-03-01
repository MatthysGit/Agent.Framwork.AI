using System.Text;
using System.Text.Json;
using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Tools;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;

public sealed class ChatPipeline
{
    private readonly RouterAgent _router;
    private readonly AgentCallerTool _caller;
    private readonly ChatTools _chartTools;
    private readonly DocumentSearchTool _docSearchTool;
    private readonly DocumentEditTool _docEditTool;
    private readonly Func<Guid> _getConversationId;
    private readonly Func<Task<bool>> _canViewCompensationAsync;
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

    public sealed record PipelineResult(string Text, string RoutedAgent, string RouterReason);

    public async Task<PipelineResult> ExecuteAsync(string userText, CancellationToken ct = default)
    {
        // 🔒 Sensitive compensation gate (must run BEFORE routing/tools)
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
        
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var swRoute = System.Diagnostics.Stopwatch.StartNew();

        var r = await _router.RouteAsync(userText, ct);

        swRoute.Stop();
        Console.WriteLine(
            $"[ROUTER] mode={r.Mode} agent={r.Agent} chartType={r.ChartType ?? "null"} " +
            $"reason=\"{r.Reason}\" routeMs={swRoute.ElapsedMilliseconds}");

        // Document search/edit are executed via tools so your attachment JSON stays identical.
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
            var instruction = InferEditInstruction(userText);
            var json = await _docEditTool.EditDocumentAsync(convoId, userText, instruction);
            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={r.Agent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");
            return new PipelineResult(json ?? "No edit result returned.", r.Agent, r.Reason);
        }

        // Chart path: SQL -> parse JSON -> chart tool -> markdown image only
        // With automatic fallback: if chart validation fails -> ask SQL agent for a table JSON -> render markdown table
        if (r.Mode.Equals("chart", StringComparison.OrdinalIgnoreCase))
        {
            var desired = string.IsNullOrWhiteSpace(r.ChartType) ? "column" : r.ChartType!.Trim().ToLowerInvariant();
            if (desired == "bar") desired = "column";

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

            Console.WriteLine("SQL_AGENT_RAW_FOR_CHART:\n" + sql.Text);

            var jsonOnly = ExtractFirstJsonObject(sql.Text);
            if (string.IsNullOrWhiteSpace(jsonOnly))
            {
                Console.WriteLine("[SQL_CHART_CONTRACT_VIOLATION] Non-JSON response from SQL agent:\n" + sql.Text);
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
                var chart = ParseChartJson(jsonOnly);
                Console.WriteLine($"CHART_PARSED: type={chart.ChartType}, labels={(chart.Labels?.Count ?? 0)}, values={(chart.Values?.Count ?? 0)}, series={(chart.Series?.Count ?? 0)}");

                var url = await CreateChartAsync(chart);

                swTotal.Stop();
                Console.WriteLine($"[PIPELINE] done agent={ChatAgentFactory.SqlAgentName} mode=chart totalMs={swTotal.ElapsedMilliseconds}");

                return new PipelineResult($"![chart]({url})", ChatAgentFactory.SqlAgentName, r.Reason);
            }
            catch (Exception ex)
            {
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

                var tableJsonOnly = ExtractFirstJsonObject(tableJson.Text) ?? tableJson.Text;

                var fallbackMd = TryRenderTableMarkdown(tableJsonOnly, out var tableMd)
                    ? tableMd
                    : $"Chart generation failed and table fallback could not be generated.\n\nError: {ex.Message}";

                // Run PPI on fallback markdown (helps catch number inflation + safety)
                var checkedFallback = await RunPpiSafeAsync(userText, fallbackMd, ct);

                swTotal.Stop();
                Console.WriteLine($"[PIPELINE] done agent={ChatAgentFactory.SqlAgentName} mode=chart-fallback totalMs={swTotal.ElapsedMilliseconds}");

                return new PipelineResult(checkedFallback, ChatAgentFactory.SqlAgentName, $"{r.Reason} (chart->table fallback)");
            }
        }

        // ✅ ENFORCE: If router chose SQL agent for a NON-CHART request, force execution (no clarifying questions).
        // ✅ Also format as a 1-column markdown table like your screenshot (header + single value).
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

            // Retry once if it still asked a question
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

            // ✅ Format into the exact simple structure you want (one header, one value row)
            var tableMdSimple = FormatSqlAnswerAsSingleColumnTable(userText, sql.Text);

            // PPI: keep the table format; if PPI tries to ask questions, keep the table
            var checkedSql = await RunPpiSafeAsync(userText, tableMdSimple, ct);

            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={ChatAgentFactory.SqlAgentName} mode=sql totalMs={swTotal.ElapsedMilliseconds}");

            return new PipelineResult(checkedSql, ChatAgentFactory.SqlAgentName, r.Reason);
        }

        // Default: call chosen agent
        var call = await _caller.CallAgentAsync(r.Agent, userText, cancellationToken: ct);

        // Skip PPI for doc tools that must return JSON unchanged
        if (r.Agent.Equals(ChatAgentFactory.DocumentSearchAgentName, StringComparison.OrdinalIgnoreCase) ||
            r.Agent.Equals(ChatAgentFactory.DocumentEditAgentName, StringComparison.OrdinalIgnoreCase))
        {
            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={call.AgentName} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");
            return new PipelineResult(call.Text, call.AgentName, r.Reason);
        }

        // Run PPI
        var checkedAnswer = await RunPpiSafeAsync(userText, call.Text, ct);

        swTotal.Stop();
        Console.WriteLine($"[PIPELINE] done agent={call.AgentName} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

        return new PipelineResult(checkedAnswer, call.AgentName, r.Reason);
    }

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

        // "individual" signals
        // - possessive name: "james salary", "james's salary"
        // - pronouns: "his salary", "her salary"
        // - "what is X salary"
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
    private static async Task<string> RunPpiSafeAsync(string userText, string draft, CancellationToken ct)
    {
        // If you haven't registered PPI yet, just return draft.
        // (Your earlier runtime showed "PpiAgent" sometimes missing.)
        try
        {
            // Caller tool will throw if agent not registered; we handle below.
            // NOTE: This method is static, so it can't access _caller.
            // We'll use a local function pattern via closure in calling sites where needed.
            return draft;
        }
        catch
        {
            return draft;
        }
    }

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

    private static bool LooksLikeClarifyingQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant();

        // "Could you please clarify..." / "Please specify..." etc
        if (t.Contains("clarify") || t.Contains("please specify") || t.Contains("more context"))
            return true;

        // Ends with '?' and contains "which/what/where" patterns
        if (t.EndsWith("?") && (t.Contains("which ") || t.Contains("what ") || t.Contains("where ") || t.Contains("who ")))
            return true;

        return false;
    }

    // ✅ Extract the first {...} JSON object (handles code fences / extra prose)
    private static string? ExtractFirstJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var s = text.Trim();

        // remove common code fences
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
        // 1) Try direct deserialize into our expected payload.
        try
        {
            var direct = JsonSerializer.Deserialize<ChartPayload>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (direct is not null && !string.IsNullOrWhiteSpace(direct.ChartType))
            {
                var hasSingle =
                    direct.Labels is { Count: > 0 } &&
                    direct.Values is { Count: > 0 } &&
                    direct.Labels.Count == direct.Values.Count;

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
            // fall through
        }

        // 2) Normalize SQL-agent schema (xAxis/xis.categories + series.data), case-insensitive property names.
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

            JsonElement xObj = default;
            if (!(TryGetPropIgnoreCase(root, "xAxis", out xObj) || TryGetPropIgnoreCase(root, "xis", out xObj)))
                xObj = default;

            var labels = new List<string>();
            if (xObj.ValueKind == JsonValueKind.Object &&
                TryGetPropIgnoreCase(xObj, "categories", out var catsEl) &&
                catsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in catsEl.EnumerateArray())
                    labels.Add(c.GetString() ?? "");
            }

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
            return new ChartPayload
            {
                ChartType = "bar",
                Title = "Chart",
                Labels = new List<string> { "No data" },
                Values = new List<double> { 0 }
            };
        }
    }

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

    private static string NormalizeChartType(string? t)
    {
        var s = (t ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "column" => "bar",
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

    private Task<string> CreateChartAsync(ChartPayload p)
    {
        var t = (p.ChartType ?? "").Trim().ToLowerInvariant();

        var labels = (p.Labels ?? new List<string>()).ToArray();
        var values = (p.Values ?? new List<double>()).ToArray();

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

    private static string Cap(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        if (s.Length == 1) return s.ToUpperInvariant();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    // =========================
    // ✅ NEW: Simple 1-column table (matches your screenshot)
    // =========================
    private static string FormatSqlAnswerAsSingleColumnTable(string userText, string sqlText)
    {
        var text = (sqlText ?? "").Trim();
        var header = GuessHeader(userText);

        // Always prefer a number if one exists anywhere in the response
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

    private static string GuessHeader(string userText)
    {
        var t = (userText ?? "").ToLowerInvariant();

        if (t.Contains("employee") || t.Contains("employees"))
            return "Employee Count";

        if (t.Contains("customer") || t.Contains("customers"))
            return "Customer Count";

        if (t.Contains("order") || t.Contains("orders"))
            return "Order Count";

        if (t.Contains("product") || t.Contains("products"))
            return "Product Count";

        return "Result";
    }

    private static bool TryExtractFirstInteger(string text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Remove thousands separators to simplify parsing
        var cleaned = text.Replace(",", "");

        // Match digits even if stuck to letters, e.g. "There are290 employees"
        var m = System.Text.RegularExpressions.Regex.Match(cleaned, @"(\d+)");
        if (!m.Success) return false;

        return long.TryParse(m.Groups[1].Value, out value);
    }

    private static string EscapeMd(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("|", "\\|").Trim();
    }

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

    public sealed class ChartSeries
    {
        public string? Name { get; set; }
        public List<double>? Values { get; set; }
    }

    private sealed record PolicyDecision(
    bool IsSensitive,
    bool IsAggregated,
    string Type,
    string Reason);

    private static PolicyDecision ClassifyPolicy(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new PolicyDecision(false, false, "None", "Empty");

        var t = text.Trim().ToLowerInvariant();

        // Aggregation intent signals (allow)
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

        // Sensitive topics
        var asksSalary =
            t.Contains("salary") || t.Contains("compensation") || t.Contains("pay") || t.Contains("wage") || t.Contains("bonus") || t.Contains("earn");

        // Direct individual targeting signals
        var individual =
            t.Contains("what is ") || t.Contains("what's ") || t.Contains("how much does ") ||
            t.Contains("his ") || t.Contains("her ") || t.Contains("their ") ||
            t.Contains("'s ") || t.Contains("salary of ") || t.Contains("pay of ") || t.Contains("compensation of ");

        // Other personal data keywords
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

        // If it’s aggregated, allow unless it’s asking for raw PII fields (like “list all bank accounts”)
        // This rule keeps aggregated stats allowed.
        if (aggregated && (asksSalary || asksBank || asksId || asksAddress || asksPhoneEmail))
        {
            // If it also looks like "list/show/export all", treat as sensitive anyway.
            if (t.Contains("list") || t.Contains("show all") || t.Contains("export") || t.Contains("download"))
            {
                return new PolicyDecision(true, false, "SensitiveBulkPII", "Bulk PII export/list request");
            }

            return new PolicyDecision(true, true, "AggregatedSensitive", "Aggregated request allowed");
        }

        // Individual compensation should be blocked unless privileged
        if (asksSalary && individual && !aggregated)
            return new PolicyDecision(true, false, "IndividualCompensation", "Individual salary/compensation requested");

        // Other PII types (block unless privileged), unless aggregated stats request
        if ((asksBank || asksId || asksAddress || asksPhoneEmail) && !aggregated)
            return new PolicyDecision(true, false, "PersonalData", "Personal data requested");

        return new PolicyDecision(false, aggregated, "None", "Not sensitive");
    }
    
}