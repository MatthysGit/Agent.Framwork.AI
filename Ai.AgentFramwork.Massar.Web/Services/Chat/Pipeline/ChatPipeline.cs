using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Services.Chat.DecisionTracking;
using Ai.AgentFramwork.Massar.Web.Tools;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;

public sealed class ChatPipeline
{
    // ----------------------------
    // Dependencies (injected)
    // ----------------------------
    private readonly RouterAgent _router;
    private readonly AgentCallerTool _caller;
    private readonly ChatTools _chartTools;
    private readonly DocumentSearchTool _docSearchTool;
    private readonly DocumentEditTool _docEditTool;
    private readonly Func<Guid> _getConversationId;
    private readonly Func<Task<bool>> _canViewCompensationAsync;
    private readonly Func<Task<bool>> _isPrivilegedAsync;

    // ✅ Decision tracking
    private readonly IDecisionTrackerService _decisionTracker;

    // Optional: allow ChatService to pass the current user id (claims subject, etc.)
    private readonly Func<string?>? _getOwnerUserId;

    public ChatPipeline(
        RouterAgent router,
        AgentCallerTool caller,
        ChatTools chartTools,
        DocumentSearchTool docSearchTool,
        DocumentEditTool docEditTool,
        Func<Guid> getConversationId,
        Func<Task<bool>> canViewCompensationAsync,
        Func<Task<bool>> isPrivilegedAsync,
        IDecisionTrackerService decisionTracker,
        Func<string?>? getOwnerUserId = null)
    {
        _router = router;
        _caller = caller;
        _chartTools = chartTools;
        _docSearchTool = docSearchTool;
        _docEditTool = docEditTool;
        _getConversationId = getConversationId;
        _canViewCompensationAsync = canViewCompensationAsync;
        _isPrivilegedAsync = isPrivilegedAsync;

        _decisionTracker = decisionTracker;
        _getOwnerUserId = getOwnerUserId;
    }

    /// <summary>
    /// Final output returned to the chat service/UI.
    /// - Text: final assistant content
    /// - RoutedAgent: the agent name chosen/executed (or "PolicyGuard")
    /// - RouterReason: explanation from the router (or policy reason)
    /// - DecisionCandidate: optional decision tracking payload for UI dialog
    /// </summary>
    public sealed record PipelineResult(
        string Text,
        string RoutedAgent,
        string RouterReason,
        DecisionCandidate? DecisionCandidate = null);

    /// <summary>
    /// UI-friendly decision candidate: the UI can show a dialog and, on confirm,
    /// call a backend endpoint to persist the draft.
    /// </summary>
    public sealed record DecisionCandidate(
        string Prompt,
        DecisionDetection Detection,
        DecisionDraft? Draft);

    public async Task<PipelineResult> ExecuteAsync(string userText, CancellationToken ct = default)
    {
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

        // ----------------------------
        // Doc tools return JSON unchanged
        // ----------------------------
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

        // ----------------------------
        // Chart path (SQL -> JSON -> chart tool -> markdown image; attach hidden payload for email)
        // ----------------------------
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

                // ✅ show the chart + attach hidden payload so Improve/Email can include it later
                var text = $"![chart]({url})";

                var chartPayload = JsonSerializer.Serialize(new
                {
                    type = "chart_url",
                    url
                });
                text += WrapHiddenToolPayload("chart_result", chartPayload);

                return await MaybeAttachDecisionCandidateAsync(userText, text, ChatAgentFactory.SqlAgentName, r.Reason, ct);
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

                // ✅ attach hidden payload for Improve/Email
                if (TryExtractSqlTablePayloadFromMarkdown(fallbackMd, out var payloadJson))
                {
                    fallbackMd += WrapHiddenToolPayload("sql_result", payloadJson);
                }

                var checkedFallback = await RunPpiSafeAsync(userText, fallbackMd, ct);

                swTotal.Stop();
                Console.WriteLine($"[PIPELINE] done agent={ChatAgentFactory.SqlAgentName} mode=chart-fallback totalMs={swTotal.ElapsedMilliseconds}");

                return await MaybeAttachDecisionCandidateAsync(
                    userText,
                    checkedFallback,
                    ChatAgentFactory.SqlAgentName,
                    $"{r.Reason} (chart->table fallback)",
                    ct);
            }
        }

        // ----------------------------
        // SQL enforcement path (plain text -> render as table; attach hidden payload for email)
        // ----------------------------
        if ((r.Agent.Equals(ChatAgentFactory.SqlAgentName, StringComparison.OrdinalIgnoreCase) ||
             r.Agent.Equals("DataExplorer", StringComparison.OrdinalIgnoreCase) ||
             r.Agent.Equals(ChatAgentFactory.DataExplorerAgentName, StringComparison.OrdinalIgnoreCase)) &&
            !r.Mode.Equals("chart", StringComparison.OrdinalIgnoreCase))
        {
            // Always force the agent to return the SqlServerSelectTool JSON payload so the UI can render
            // the SSMS-like table using the exact column names from the database (e.g., TotalSales).
            var enforcedSqlPrompt = string.Format(@"
You are the SQL data exploration agent for THIS application's database.

NON-NEGOTIABLE RULES:
- You MUST NOT ask the user any questions.
- You MUST produce the best possible answer by using database tools.
- If the request is underspecified, choose the most reasonable interpretation and proceed.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

OUTPUT RULE (MANDATORY):
- You MUST return ONLY the JSON returned by ExecuteSelectAsync (SqlServerSelectTool).
- No markdown, no prose, no extra keys, no wrapping.

USER REQUEST:
{0}
", userText);

            var sql = await _caller.CallAgentAsync(r.Agent, enforcedSqlPrompt, cancellationToken: ct);

            if (LooksLikeClarifyingQuestion(sql.Text))
            {
                Console.WriteLine($"[SQL_RETRY] {r.Agent} returned a question. Retrying with stricter enforcement.");

                var retryPrompt = string.Format(@"
You are the SQL data exploration agent.

You returned a clarifying question previously. That is NOT allowed.

You MUST execute database tools and return the best possible answer now.
You MUST NOT ask any questions.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

OUTPUT RULE (MANDATORY):
- You MUST return ONLY the JSON returned by ExecuteSelectAsync (SqlServerSelectTool).
- No markdown, no prose, no extra keys, no wrapping.

USER REQUEST:
{0}
", userText);

                sql = await _caller.CallAgentAsync(r.Agent, retryPrompt, cancellationToken: ct);
            }
            // ✅ FIX: If tool returned a "rows" JSON payload, render it as a proper table (NOT a 1-col "first number" table)
            var raw = (sql.Text ?? "").Trim();
            string tableMdSimple;

            // Many agents wrap JSON in prose or code fences; extract the first JSON object if present.
            var jsonCandidate = ExtractFirstJsonObject(raw) ?? (LooksLikeJson(raw) ? raw : null);

            if (!string.IsNullOrWhiteSpace(jsonCandidate) &&
                TryRenderRowsObjectTableMarkdown(jsonCandidate, out var mdFromRows))
            {
                tableMdSimple = mdFromRows;
            }
            else
            {
                tableMdSimple = FormatSqlAnswerAsSingleColumnTable(userText, raw);
            }

            // ✅ attach hidden payload so Improve/Email can reliably extract it
            if (TryExtractSqlTablePayloadFromMarkdown(tableMdSimple, out var payloadJson))
            {
                tableMdSimple += WrapHiddenToolPayload("sql_result", payloadJson);
            }

            var checkedSql = await RunPpiSafeAsync(userText, tableMdSimple, ct);

            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={r.Agent} mode=sql totalMs={swTotal.ElapsedMilliseconds}");

            return await MaybeAttachDecisionCandidateAsync(userText, checkedSql, r.Agent, r.Reason, ct);
        }

        // ----------------------------
        // Default: call chosen agent
        // ----------------------------
        var call = await _caller.CallAgentAsync(r.Agent, userText, cancellationToken: ct);

        if (r.Agent.Equals(ChatAgentFactory.DocumentSearchAgentName, StringComparison.OrdinalIgnoreCase) ||
            r.Agent.Equals(ChatAgentFactory.DocumentEditAgentName, StringComparison.OrdinalIgnoreCase))
        {
            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={call.AgentName} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");
            return new PipelineResult(call.Text, call.AgentName, r.Reason);
        }

        var checkedAnswer = await RunPpiSafeAsync(userText, call.Text, ct);

        swTotal.Stop();
        Console.WriteLine($"[PIPELINE] done agent={call.AgentName} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

        return await MaybeAttachDecisionCandidateAsync(userText, checkedAnswer, call.AgentName, r.Reason, ct);
    }

    // ✅ Decision hook: attach candidate (no text mutation)

    private async Task<PipelineResult> MaybeAttachDecisionCandidateAsync(
        string userText,
        string assistantText,
        string routedAgent,
        string routerReason,
        CancellationToken ct)
    {
        // Don’t attempt decision tracking on JSON attachments responses
        if (LooksLikeJson(assistantText))
            return new PipelineResult(assistantText, routedAgent, routerReason);

        // ✅ Never run decision detection on meta turns (prevents infinite loops)
        if (IsDecisionTrackerMetaTurn(userText) || IsDecisionTrackerMetaTurn(assistantText))
            return new PipelineResult(assistantText, routedAgent, routerReason);

        Guid? convoId = null;
        try { convoId = _getConversationId(); } catch { /* ignore */ }

        var detection = await _decisionTracker.DetectAsync(userText, assistantText, convoId, ct);
        if (detection is null)
            return new PipelineResult(assistantText, routedAgent, routerReason);

        var ownerUserId = _getOwnerUserId?.Invoke();

        // Draft is optional; if it fails, UI can still record manually
        var draft = await _decisionTracker.BuildDraftAsync(userText, assistantText, detection, convoId, ownerUserId, ct);

        var prompt = "Do you want me to record this as a formal decision?";

        return new PipelineResult(
            Text: assistantText,
            RoutedAgent: routedAgent,
            RouterReason: routerReason,
            DecisionCandidate: new DecisionCandidate(prompt, detection, draft));
    }

    private static bool IsDecisionTrackerMetaTurn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.Trim().ToLowerInvariant();

        if (t.Contains("potential decision detected")) return true;
        if (t.Contains("record this as a formal decision")) return true;

        if (t == "yes" || t == "y" || t == "yeah" || t == "yep") return true;
        if (t == "no" || t == "n" || t == "nope") return true;

        if (t.Contains("🟡") && t.Contains("decision")) return true;

        return false;
    }

    private static bool LooksLikeJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.TrimStart();
        return t.StartsWith("{") || t.StartsWith("[");
    }

    // Heuristic: if the user is asking for a breakdown/list/grouped result, we want a table (multi-row),
    // and we should keep the SQL tool JSON so the UI can render a proper table.
    private static bool LooksTabularUserRequest(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return false;
        var t = userText.Trim().ToLowerInvariant();

        // Strong signals for multi-row output
        if (t.Contains("group by") || t.Contains("breakdown") || t.Contains("by region") || t.Contains("by department") ||
            t.Contains("by office") || t.Contains("by country") || t.Contains("per ") || t.Contains("top ") ||
            t.Contains("list ") || t.StartsWith("list") || t.Contains("show ") || t.StartsWith("show") ||
            t.Contains("each ") || t.Contains("all ") || t.Contains("regions") || t.Contains("departments") ||
            t.Contains("countries") || t.Contains("offices"))
            return true;

        // If asking for "count ... by ..." in natural language
        if (t.Contains("count") && t.Contains(" by "))
            return true;

        return false;
    }

    private static bool IsIndividualCompensationQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.ToLowerInvariant();

        var comp =
            t.Contains("salary") ||
            t.Contains("compensation") ||
            t.Contains("pay") ||
            t.Contains("wage") ||
            t.Contains("bonus") ||
            t.Contains("earn");

        if (!comp) return false;

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
        try { return draft; }
        catch { return draft; }
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

        if (t.Contains("clarify") || t.Contains("please specify") || t.Contains("more context"))
            return true;

        if (t.EndsWith("?") && (t.Contains("which ") || t.Contains("what ") || t.Contains("where ") || t.Contains("who ")))
            return true;

        return false;
    }

    private static string? ExtractFirstJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var s = text.Trim();

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
        // 1) Attempt direct deserialize into ChartPayload.
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

    // ✅ NEW: Render a SqlServerSelectTool-style payload:
    // {"rowCount":10,"truncated":false,"rows":[{"ColA":"x","ColB":1}, ... ]}
    private static bool TryRenderRowsObjectTableMarkdown(string json, out string markdown)
    {
        markdown = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!TryGetPropIgnoreCase(root, "rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                return false;

            var rows = rowsEl.EnumerateArray().ToList();
            if (rows.Count == 0) return false;

            if (rows.Any(r => r.ValueKind != JsonValueKind.Object))
                return false;

            var columns = new List<string>();
            void AddCol(string name)
            {
                if (!columns.Contains(name, StringComparer.OrdinalIgnoreCase))
                    columns.Add(name);
            }

            foreach (var p in rows[0].EnumerateObject())
                AddCol(p.Name);

            foreach (var r in rows)
                foreach (var p in r.EnumerateObject())
                    AddCol(p.Name);

            if (columns.Count == 0) return false;

            var sb = new StringBuilder();
            sb.AppendLine("| " + string.Join(" | ", columns.Select(EscapeMd)) + " |");
            sb.AppendLine("| " + string.Join(" | ", columns.Select(_ => "---")) + " |");

            foreach (var r in rows)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in r.EnumerateObject())
                {
                    map[p.Name] = p.Value.ValueKind == JsonValueKind.String
                        ? (p.Value.GetString() ?? "")
                        : p.Value.ToString();
                }

                var cells = columns.Select(c => EscapeMd(map.TryGetValue(c, out var v) ? v : "")).ToArray();
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

    private static string WrapHiddenToolPayload(string type, string json)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json ?? ""));
        return $"\n\n[//]: # (TOOL:{type}:{b64})\n";
    }

    private static bool TryExtractSqlTablePayloadFromMarkdown(string markdown, out string payloadJson)
    {
        payloadJson = "";
        if (string.IsNullOrWhiteSpace(markdown)) return false;

        var t = markdown.TrimStart();
        if (t.StartsWith("{") || t.StartsWith("["))
        {
            payloadJson = markdown;
            return true;
        }

        payloadJson = JsonSerializer.Serialize(new
        {
            type = "markdown_table",
            markdown = markdown
        });

        return true;
    }

    private static string Cap(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        if (s.Length == 1) return s.ToUpperInvariant();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    // =========================
    // ✅ Simple 1-column table
    // =========================

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

    private static string GuessHeader(string userText)
    {
        var text = (userText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "Result";

        var lower = Regex.Replace(text.ToLowerInvariant(), @"\s+", " ");

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

        if (lower.Contains("employee")) return "Employee Count";
        if (lower.Contains("customer")) return "Customer Count";
        if (lower.Contains("order")) return "Order Count";
        if (lower.Contains("product")) return "Product Count";

        return "Result";
    }

    private static string CleanupThing(string thing)
    {
        thing = (thing ?? "").Trim();
        thing = thing.TrimEnd('?', '.', '!', ',', ';', ':');

        thing = Regex.Replace(
            thing,
            @"\b(do|does)\s+\w+\s+have\b.*$|\bare\s+there\b.*$|\bis\s+there\b.*$",
            "",
            RegexOptions.IgnoreCase).Trim();

        thing = Regex.Replace(
            thing,
            @"\b(in|for|by|per|with|where|that|who|which|from|between|during|since)\b.*$",
            "",
            RegexOptions.IgnoreCase).Trim();

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

    private static bool TryExtractFirstInteger(string text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var cleaned = text.Replace(",", "");
        var m = Regex.Match(cleaned, @"(\d+)");
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

        public double? Value { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
    }

    public sealed class ChartSeries
    {
        public string? Name { get; set; }
        public List<double>? Values { get; set; }
    }

    // --------------------------------------------------------------------
    // Policy classification (currently unused by ExecuteAsync in this version)
    // --------------------------------------------------------------------

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
