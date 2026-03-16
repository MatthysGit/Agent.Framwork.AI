using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.Services.Anomaly;
using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Services.Chat.DecisionTracking;
using Ai.AgentFramwork.Massar.Web.Services.Forecasting;
using Ai.AgentFramwork.Massar.Web.Services.WhatIfs;
using Ai.AgentFramwork.Massar.Web.Services.WhatIfs.Modelss;
using Ai.AgentFramwork.Massar.Web.Tools;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;

public sealed partial class ChatPipeline
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



    //

    // ✅ Decision tracking
    private readonly IDecisionTrackerService _decisionTracker;

    // ✅ Route-flow tracking
    private readonly RouterDecisionTrackerService? _routerDecisionTracker;

    // Optional: allow ChatService to pass the current user id (claims subject, etc.)
    private readonly Func<string?>? _getOwnerUserId;

    //private const string ExecutiveInsightAgentName = "ExecutiveInsightAgent";
    //private const string ForecastingAgentName = "ForecastingAgent";
    //private const string AnomalyDetectionAgentName = "AnomalyDetectionAgent";

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
        RouterDecisionTrackerService? routerDecisionTracker = null,
        Func<string?>? getOwnerUserId = null
    )
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
        _routerDecisionTracker = routerDecisionTracker;
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

    private Task TrackRouteAsync(string stage, string detail, CancellationToken ct = default)
        => _routerDecisionTracker is null
            ? Task.CompletedTask
            : _routerDecisionTracker.TrackAsync(stage, detail, ct);

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

        await TrackRouteAsync("RouterAgent", $"selected={r.Agent}; mode={r.Mode}; reason={r.Reason}", ct);



        // ----------------------------
        // Doc tools return JSON unchanged
        // ----------------------------
        if (r.Agent.Equals(ChatAgentFactory.DocumentSearchAgentName, StringComparison.OrdinalIgnoreCase))
        {
            await TrackRouteAsync(r.Agent, "entered", ct);


            var convoId = _getConversationId();
            var json = await _docSearchTool.SearchDocumentsAsync(convoId, userText);

            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={r.Agent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return new PipelineResult(json ?? "No relevant information found.", r.Agent, r.Reason);
        }

        if (r.Agent.Equals(ChatAgentFactory.DocumentEditAgentName, StringComparison.OrdinalIgnoreCase))
        {
            await TrackRouteAsync(r.Agent, "entered", ct);



            var convoId = _getConversationId();
            var instruction = InferEditInstruction(userText);

            var json = await _docEditTool.EditDocumentAsync(convoId, userText, instruction);

            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={r.Agent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return new PipelineResult(json ?? "No edit result returned.", r.Agent, r.Reason);
        }


        // ----------------------------
        // Executive insight orchestration path
        // ----------------------------
        if (string.Equals(r.Agent, ChatAgentFactory.ExecutiveInsightAgentName, StringComparison.OrdinalIgnoreCase))
        {
            await TrackRouteAsync(r.Agent, "entered", ct);


            var executiveResult = await ExecuteExecutiveInsightAsync(userText, r.Reason, ct);

            swTotal.Stop();
            Console.WriteLine(
                $"[PIPELINE] done agent={executiveResult.RoutedAgent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return executiveResult;
        }

        if (string.Equals(r.Agent, ChatAgentFactory.DataIntelligenceAgentName, StringComparison.OrdinalIgnoreCase))
        {

            await TrackRouteAsync(r.Agent, "entered", ct);



            var dataIntelligenceResult = await ExecuteDataIntelligenceAsync(userText, r.Reason, ct);

            swTotal.Stop();
            Console.WriteLine(
                $"[PIPELINE] done agent={dataIntelligenceResult.RoutedAgent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return dataIntelligenceResult;
        }

        if (string.Equals(r.Agent, ChatAgentFactory.ForecastingAgentName, StringComparison.OrdinalIgnoreCase))
        {
            await TrackRouteAsync(r.Agent, "entered", ct);


            var forecastResult = await ExecuteForecastAsync(userText, r.Reason, ct);

            swTotal.Stop();
            Console.WriteLine(
                $"[PIPELINE] done agent={forecastResult.RoutedAgent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return forecastResult;
        }

        if (string.Equals(r.Agent, ChatAgentFactory.AnomalyDetectionAgentName, StringComparison.OrdinalIgnoreCase))
        {
            await TrackRouteAsync(r.Agent, "entered", ct);


            var anomalyResult = await ExecuteAnomalyDetectionAsync(userText, r.Reason, ct);

            swTotal.Stop();
            Console.WriteLine(
                $"[PIPELINE] done agent={anomalyResult.RoutedAgent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return anomalyResult;
        }

        if (string.Equals(r.Agent, "DataSegmentationAgent", StringComparison.OrdinalIgnoreCase))
        {
            await TrackRouteAsync(r.Agent, "entered", ct);



            var segmentationResult = await ExecuteSegmentationAsync(userText, r.Reason, ct);

            swTotal.Stop();
            Console.WriteLine(
                $"[PIPELINE] done agent={segmentationResult.RoutedAgent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return segmentationResult;
        }

        if (string.Equals(r.Agent, ChatAgentFactory.WhatIfSimulationAgentName, StringComparison.OrdinalIgnoreCase))
        {
            await TrackRouteAsync(r.Agent, "entered", ct);



            var whatIfResult = await ExecuteWhatIfSimulationAsync(userText, r.Reason, ct);

            swTotal.Stop();
            Console.WriteLine(
                $"[PIPELINE] done agent={whatIfResult.RoutedAgent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return whatIfResult;
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

            await TrackRouteAsync(ChatAgentFactory.SqlAgentName, "entered", ct);
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
                Console.WriteLine(
                    $"CHART_PARSED: type={chart.ChartType}, labels={(chart.Labels?.Count ?? 0)}, values={(chart.Values?.Count ?? 0)}, series={(chart.Series?.Count ?? 0)}");

                var url = await CreateChartAsync(chart);

                swTotal.Stop();
                Console.WriteLine(
                    $"[PIPELINE] done agent={ChatAgentFactory.SqlAgentName} mode=chart totalMs={swTotal.ElapsedMilliseconds}");

                // ✅ show the chart + attach hidden payload so Improve/Email can include it later
                var text = $"![chart]({url})";

                var chartPayload = JsonSerializer.Serialize(new
                {
                    type = "chart_url",
                    url
                });
                text += WrapHiddenToolPayload("chart_result", chartPayload);

                return await MaybeAttachDecisionCandidateAsync(userText, text, ChatAgentFactory.SqlAgentName, r.Reason,
                    ct);
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

                await TrackRouteAsync(ChatAgentFactory.SqlAgentName, "entered", ct);
                var tableJson =
                    await _caller.CallAgentAsync(ChatAgentFactory.SqlAgentName, tablePrompt, cancellationToken: ct);
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
                Console.WriteLine(
                    $"[PIPELINE] done agent={ChatAgentFactory.SqlAgentName} mode=chart-fallback totalMs={swTotal.ElapsedMilliseconds}");

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
        var isSqlAgent = r.Agent.Equals(ChatAgentFactory.SqlAgentName, StringComparison.OrdinalIgnoreCase);
        var isDataExplorerAgent =
            r.Agent.Equals("DataExplorer", StringComparison.OrdinalIgnoreCase) ||
            r.Agent.Equals(ChatAgentFactory.DataExplorerAgentName, StringComparison.OrdinalIgnoreCase);
        var forceSqlForDataExplorer = isDataExplorerAgent && ShouldForceSqlDataExplorer(userText);

        if ((isSqlAgent || forceSqlForDataExplorer) &&
            !r.Mode.Equals("chart", StringComparison.OrdinalIgnoreCase))
        {
            await TrackRouteAsync(r.Agent, "entered", ct);


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
        await TrackRouteAsync(r.Agent, "entered", ct);
        var call = await _caller.CallAgentAsync(r.Agent, userText, cancellationToken: ct);

        if (r.Agent.Equals(ChatAgentFactory.DocumentSearchAgentName, StringComparison.OrdinalIgnoreCase) ||
            r.Agent.Equals(ChatAgentFactory.DocumentEditAgentName, StringComparison.OrdinalIgnoreCase))
        {
            await TrackRouteAsync(r.Agent, "entered", ct);

            swTotal.Stop();
            Console.WriteLine(
                $"[PIPELINE] done agent={call.AgentName} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");
            return new PipelineResult(call.Text, call.AgentName, r.Reason);
        }

        var checkedAnswer = await RunPpiSafeAsync(userText, call.Text, ct);

        swTotal.Stop();
        Console.WriteLine(
            $"[PIPELINE] done agent={call.AgentName} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

        return await MaybeAttachDecisionCandidateAsync(userText, checkedAnswer, call.AgentName, r.Reason, ct);
    }
}

