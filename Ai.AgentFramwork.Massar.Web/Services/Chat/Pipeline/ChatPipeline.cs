using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Services.Chat.DecisionTracking;
using Ai.AgentFramwork.Massar.Web.Services.Forecasting;
using Ai.AgentFramwork.Massar.Web.Tools;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ai.AgentFramwork.Massar.Web.Services.Anomaly;
using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;
using Ai.AgentFramwork.Massar.Web.Services.WhatIfs;
using Ai.AgentFramwork.Massar.Web.Services.WhatIfs.Modelss;

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
        // Executive insight orchestration path
        // ----------------------------
        if (string.Equals(r.Agent, ChatAgentFactory.ExecutiveInsightAgentName, StringComparison.OrdinalIgnoreCase))
        {
            var executiveResult = await ExecuteExecutiveInsightAsync(userText, r.Reason, ct);

            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={executiveResult.RoutedAgent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return executiveResult;
        }

        if (string.Equals(r.Agent, ChatAgentFactory.DataIntelligenceAgentName, StringComparison.OrdinalIgnoreCase))
        {
            var dataIntelligenceResult = await ExecuteDataIntelligenceAsync(userText, r.Reason, ct);

            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={dataIntelligenceResult.RoutedAgent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return dataIntelligenceResult;
        }

        if (string.Equals(r.Agent, ChatAgentFactory.ForecastingAgentName, StringComparison.OrdinalIgnoreCase))
        {
            var forecastResult = await ExecuteForecastAsync(userText, r.Reason, ct);

            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={forecastResult.RoutedAgent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return forecastResult;
        }

        if (string.Equals(r.Agent, ChatAgentFactory.AnomalyDetectionAgentName, StringComparison.OrdinalIgnoreCase))
        {
            var anomalyResult = await ExecuteAnomalyDetectionAsync(userText, r.Reason, ct);

            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={anomalyResult.RoutedAgent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return anomalyResult;
        }

        if (string.Equals(r.Agent, "DataSegmentationAgent", StringComparison.OrdinalIgnoreCase))
        {
            var segmentationResult = await ExecuteSegmentationAsync(userText, r.Reason, ct);

            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={segmentationResult.RoutedAgent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

            return segmentationResult;
        }

        if (string.Equals(r.Agent, ChatAgentFactory.WhatIfSimulationAgentName, StringComparison.OrdinalIgnoreCase))
        {
            var whatIfResult = await ExecuteWhatIfSimulationAsync(userText, r.Reason, ct);

            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={whatIfResult.RoutedAgent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

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


    private async Task<PipelineResult> ExecuteDataIntelligenceAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        var supportPrompt = BuildDataIntelligenceSupportDataPrompt(userText);

        var support = await _caller.CallAgentAsync(
            ChatAgentFactory.DataExplorerAgentName,
            supportPrompt,
            cancellationToken: ct);

        var supportText = (support.Text ?? string.Empty).Trim();

        if (LooksLikeClarifyingQuestion(supportText))
        {
            Console.WriteLine("[DATA_INTELLIGENCE_RETRY] DataExplorer returned a question. Retrying with stricter enforcement.");

            var retryPrompt = BuildDataIntelligenceSupportDataPrompt(userText, stricter: true);

            support = await _caller.CallAgentAsync(
                ChatAgentFactory.DataExplorerAgentName,
                retryPrompt,
                cancellationToken: ct);

            supportText = (support.Text ?? string.Empty).Trim();
        }

        var finalSupportText = PrepareDataIntelligenceSupportPayload(userText, supportText);

        var synthesisPrompt = BuildDataIntelligenceSynthesisPrompt(userText, finalSupportText);

        var intelligence = await _caller.CallAgentAsync(
            ChatAgentFactory.DataIntelligenceAgentName,
            synthesisPrompt,
            cancellationToken: ct);

        var intelligenceText = (intelligence.Text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(intelligenceText))
        {
            intelligenceText =
                "## Data Intelligence Summary\n" +
                "I couldn’t produce a grounded data intelligence analysis from the available data.\n\n" +
                "## What the data suggests\n" +
                "- No reliable support payload was returned from the database.\n\n" +
                "## Key signals / patterns\n" +
                "- There was not enough structured evidence to identify meaningful patterns.\n\n" +
                "## Risks or watchouts\n" +
                "- Decisions based on incomplete data may be misleading.\n\n" +
                "## Recommended actions\n" +
                "- Ask for a more targeted analytical request such as sales by region, revenue by month, margin by category, or orders by status.";
        }

        intelligenceText += WrapHiddenToolPayload(
            "data_intelligence_result",
            JsonSerializer.Serialize(new
            {
                type = "data_intelligence",
                question = userText,
                supportPayload = finalSupportText
            }));

        var checkedAnswer = await RunPpiSafeAsync(userText, intelligenceText, ct);

        return await MaybeAttachDecisionCandidateAsync(
            userText,
            checkedAnswer,
            ChatAgentFactory.DataIntelligenceAgentName,
            routerReason + " (grounded via DataExplorerAgent)",
            ct);
    }

    private async Task<PipelineResult> ExecuteExecutiveInsightAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        var supportPrompt = BuildExecutiveSupportDataPrompt(userText);

        var support = await _caller.CallAgentAsync(
            ChatAgentFactory.DataExplorerAgentName,
            supportPrompt,
            cancellationToken: ct);

        var supportText = (support.Text ?? string.Empty).Trim();

        if (LooksLikeClarifyingQuestion(supportText))
        {
            Console.WriteLine("[EXECUTIVE_RETRY] DataExplorer returned a question. Retrying with stricter enforcement.");

            var retryPrompt = BuildExecutiveSupportDataPrompt(userText, stricter: true);

            support = await _caller.CallAgentAsync(
                ChatAgentFactory.DataExplorerAgentName,
                retryPrompt,
                cancellationToken: ct);

            supportText = (support.Text ?? string.Empty).Trim();
        }

        var finalSupportText = PrepareExecutiveSupportPayload(userText, supportText);

        var executivePrompt = BuildExecutiveSynthesisPrompt(userText, finalSupportText);

        var executive = await _caller.CallAgentAsync(
            ChatAgentFactory.ExecutiveInsightAgentName,
            executivePrompt,
            cancellationToken: ct);

        var executiveText = (executive.Text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(executiveText))
        {
            executiveText = "Executive Summary\nI couldn’t generate a grounded executive summary from the available data.\n\nKey Insights\n• No supporting data was returned from the database.\n\nRisks\n• Leadership decisions may be delayed without supporting metrics.\n\nOpportunities\n• Run a more specific business-performance query to gather supporting facts.\n\nRecommended Actions\n• Ask for a focused executive summary such as sales by territory, revenue trend, or headcount by department.";
        }

        var checkedAnswer = await RunPpiSafeAsync(userText, executiveText, ct);

        return await MaybeAttachDecisionCandidateAsync(
            userText,
            checkedAnswer,
            ChatAgentFactory.ExecutiveInsightAgentName,
            routerReason + " (grounded via DataExplorerAgent)",
            ct);
    }


    private async Task<PipelineResult> ExecuteForecastAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        var request = ForecastingService.ParseRequest(userText);

        var historicalPrompt = BuildForecastSupportDataPrompt(userText, request);

        var support = await _caller.CallAgentAsync(
            ChatAgentFactory.DataExplorerAgentName,
            historicalPrompt,
            cancellationToken: ct);

        var supportText = (support.Text ?? string.Empty).Trim();

        if (LooksLikeClarifyingQuestion(supportText))
        {
            var retryPrompt = BuildForecastSupportDataPrompt(userText, request, stricter: true);

            support = await _caller.CallAgentAsync(
                ChatAgentFactory.DataExplorerAgentName,
                retryPrompt,
                cancellationToken: ct);

            supportText = (support.Text ?? string.Empty).Trim();
        }

        if (!TryParseForecastInputPoints(supportText, out var inputPoints))
        {
            var failText = "I couldn't generate a forecast because the supporting query did not return a usable time series. Please ask for a forecast with a date-based metric such as monthly sales, revenue, or orders.";
            return await MaybeAttachDecisionCandidateAsync(userText, failText, ChatAgentFactory.ForecastingAgentName, routerReason, ct);
        }

        var forecast = ForecastingService.GenerateForecast(request, inputPoints);
        var markdown = BuildForecastMarkdown(forecast);

        try
        {
            var chartUrl = await CreateForecastChartAsync(forecast);
            markdown = $"![chart]({chartUrl})\n\n" + markdown;
            markdown += WrapHiddenToolPayload("chart_result", JsonSerializer.Serialize(new { type = "chart_url", url = chartUrl }));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[FORECAST_CHART_FALLBACK] " + ex);
        }

        markdown += WrapHiddenToolPayload("forecast_result", BuildForecastHiddenPayload(forecast));

        var checkedAnswer = await RunPpiSafeAsync(userText, markdown, ct);

        return await MaybeAttachDecisionCandidateAsync(
            userText,
            checkedAnswer,
            ChatAgentFactory.ForecastingAgentName,
            routerReason + " (grounded via DataExplorerAgent)",
            ct);
    }


    private async Task<PipelineResult> ExecuteAnomalyDetectionAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        var request = AnomalyRequestParser.Parse(userText);
        var supportPrompt = AnomalySupportPromptBuilder.Build(request);

        var support = await _caller.CallAgentAsync(
            ChatAgentFactory.SqlAgentName,
            supportPrompt,
            cancellationToken: ct);

        var supportText = (support.Text ?? string.Empty).Trim();

        if (LooksLikeClarifyingQuestion(supportText))
        {
            var retryPrompt = AnomalySupportPromptBuilder.Build(request, stricter: true);
            support = await _caller.CallAgentAsync(
                ChatAgentFactory.SqlAgentName,
                retryPrompt,
                cancellationToken: ct);
            supportText = (support.Text ?? string.Empty).Trim();
        }

        if (!AnomalyInputParser.TryParse(supportText, request.MetricName, request.GroupBy, out var dataset))
        {
            var failText = "I couldn't run anomaly detection because the supporting query did not return a usable time series. Please ask for anomalies on a date-based metric such as monthly sales, revenue, orders, cost, or headcount.";
            return await MaybeAttachDecisionCandidateAsync(userText, failText, ChatAgentFactory.AnomalyDetectionAgentName, routerReason, ct);
        }

        var coordinator = new AnomalyDetectionCoordinator();
        var result = coordinator.Execute(request, dataset);
        var markdown = result.Narrative;

        if (result.TableColumns.Count > 0 && result.TableRows.Count > 0)
            markdown += "\n\n" + BuildMarkdownTable(result.TableColumns, result.TableRows);

        try
        {
            var chartUrl = await CreateAnomalyChartAsync(result);
            markdown = $"![chart]({chartUrl})\n\n" + markdown;
            markdown += WrapHiddenToolPayload("chart_result", JsonSerializer.Serialize(new { type = "chart_url", url = chartUrl }));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ANOMALY_CHART_FALLBACK] " + ex);
        }

        markdown += WrapHiddenToolPayload("anomaly_result", AnomalyHiddenPayloadBuilder.Build(result));

        var checkedAnswer = await RunPpiSafeAsync(userText, markdown, ct);

        return await MaybeAttachDecisionCandidateAsync(
            userText,
            checkedAnswer,
            ChatAgentFactory.AnomalyDetectionAgentName,
            routerReason + " (grounded via DataExplorerAgent)",
            ct);
    }


    private async Task<PipelineResult> ExecuteSegmentationAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        var supportPrompt = BuildSegmentationSupportDataPrompt(userText);

        var support = await _caller.CallAgentAsync(
            ChatAgentFactory.SqlAgentName,
            supportPrompt,
            cancellationToken: ct);

        var supportText = (support.Text ?? string.Empty).Trim();

        if (LooksLikeClarifyingQuestion(supportText))
        {
            Console.WriteLine("[SEGMENTATION_RETRY] SqlAgent returned a question. Retrying with stricter enforcement.");

            var retryPrompt = BuildSegmentationSupportDataPrompt(userText, stricter: true);

            support = await _caller.CallAgentAsync(
                ChatAgentFactory.SqlAgentName,
                retryPrompt,
                cancellationToken: ct);

            supportText = (support.Text ?? string.Empty).Trim();
        }

        if (!TryParseSegmentationRows(supportText, out var rows, out var parseReason))
        {
            var failText = "I couldn't run segmentation because the supporting query did not return a usable grouped dataset. Please ask for a segmentation such as sales by region, revenue by category, orders by status, or headcount by department.";
            return await MaybeAttachDecisionCandidateAsync(userText, failText, "DataSegmentationAgent", routerReason + $" ({parseReason})", ct);
        }

        var markdown = BuildSegmentationMarkdown(userText, rows);

        try
        {
            var chartUrl = await CreateSegmentationChartAsync(userText, rows);
            markdown = $"![chart]({chartUrl})\n\n" + markdown;
            markdown += WrapHiddenToolPayload("chart_result", JsonSerializer.Serialize(new { type = "chart_url", url = chartUrl }));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[SEGMENTATION_CHART_FALLBACK] " + ex);
        }

        markdown += WrapHiddenToolPayload("segmentation_result", BuildSegmentationHiddenPayload(userText, rows));

        var checkedAnswer = await RunPpiSafeAsync(userText, markdown, ct);

        return await MaybeAttachDecisionCandidateAsync(
            userText,
            checkedAnswer,
            "DataSegmentationAgent",
            routerReason + " (grounded via SqlAgent)",
            ct);
    }

    private static string BuildSegmentationSupportDataPrompt(string userText, bool stricter = false)
    {
        var strict = stricter
            ? "- You previously asked a question. That is NOT allowed. Choose the most reasonable interpretation and proceed."
            : "- Do NOT ask follow-up questions. Choose the most reasonable interpretation and proceed.";

        return $@"
You are the SQL agent for grouped segmentation analysis.

NON-NEGOTIABLE RULES:
{strict}
- Use database tools only.
- Return a grouped result suitable for segmentation analysis.
- Prefer one clear segment dimension and one numeric metric.
- Limit the result to the most relevant 10-20 segments unless the user explicitly asks otherwise.
- Return the segments ordered by Value descending when that makes sense.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

MANDATORY OUTPUT RULES:
- Return ONLY the JSON returned by ExecuteSelectAsync.
- No markdown, no prose, no wrapping.
- The returned rows MUST represent grouped segmentation data.
- Use EXACT logical columns whenever possible:
  - Segment: the segment/group label
  - Value: the numeric measure for that segment
  - SharePct: optional percentage share of total
  - Rank: optional rank
- If exact aliases are not possible, still return one categorical grouping column and one numeric measure column.

USER REQUEST:
{userText}
";
    }

    private static bool TryParseSegmentationRows(string text, out List<SegmentationRow> rows, out string reason)
    {
        rows = new List<SegmentationRow>();
        reason = "no grouped rows parsed";

        var json = ExtractFirstJsonObject(text) ?? (LooksLikeJson(text) ? text : null);
        if (string.IsNullOrWhiteSpace(json))
        {
            reason = "no json payload";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetPropIgnoreCase(doc.RootElement, "rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
            {
                reason = "rows array missing";
                return false;
            }

            var rawRows = rowsEl.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object).ToList();
            if (rawRows.Count == 0)
            {
                reason = "rows array empty";
                return false;
            }

            foreach (var row in rawRows)
            {
                var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in row.EnumerateObject())
                    map[p.Name] = p.Value;

                if (!TryResolveSegmentLabel(map, out var segment))
                    continue;

                if (!TryResolveNumericValue(map, out var value, out var valueColumn))
                    continue;

                double? sharePct = null;
                if (TryResolveOptionalNumeric(map, new[] { "SharePct", "Share", "Percentage", "Percent", "Pct", "ContributionPct" }, out var s))
                    sharePct = s;

                int? rank = null;
                if (TryResolveOptionalInteger(map, new[] { "Rank", "RowNum", "Position" }, out var rnk))
                    rank = rnk;

                rows.Add(new SegmentationRow(segment, value, sharePct, rank, valueColumn));
            }

            if (rows.Count == 0)
            {
                reason = "no segment/value pairs found";
                return false;
            }

            rows = rows
                .OrderBy(r => r.Rank ?? int.MaxValue)
                .ThenByDescending(r => r.Value)
                .ThenBy(r => r.Segment, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var total = rows.Sum(r => r.Value);
            if (total > 0)
            {
                rows = rows.Select(r =>
                    r.SharePct.HasValue
                        ? r
                        : r with { SharePct = Math.Round((r.Value / total) * 100d, 2) })
                    .ToList();
            }

            reason = "ok";
            return true;
        }
        catch
        {
            reason = "invalid json payload";
            return false;
        }
    }

    private static string BuildSegmentationMarkdown(string userText, List<SegmentationRow> rows)
    {
        var topRows = rows.Take(20).ToList();
        var total = rows.Sum(r => r.Value);
        var top = rows.OrderByDescending(r => r.Value).First();
        var bottom = rows.OrderBy(r => r.Value).First();
        var top3Share = total > 0
            ? rows.OrderByDescending(r => r.Value).Take(3).Sum(r => r.Value) / total * 100d
            : 0d;

        var metricLabel = GuessSegmentationMetricLabel(userText, rows);
        var sb = new StringBuilder();

        sb.AppendLine("Segmentation Summary");
        sb.AppendLine($"The largest segment is **{EscapeMd(top.Segment)}** with **{FormatMetricValue(top.Value)}** {EscapeMd(metricLabel)}.");
        sb.AppendLine($"The smallest segment is **{EscapeMd(bottom.Segment)}** with **{FormatMetricValue(bottom.Value)}** {EscapeMd(metricLabel)}.");
        if (total > 0)
            sb.AppendLine($"The top 3 segments contribute **{top3Share:0.##}%** of the total.");
        sb.AppendLine();

        sb.AppendLine("Key Insights");
        sb.AppendLine($"• Total across all returned segments: **{FormatMetricValue(total)}** {EscapeMd(metricLabel)}.");
        sb.AppendLine($"• Number of returned segments: **{rows.Count}**.");
        if (top.SharePct.HasValue)
            sb.AppendLine($"• {EscapeMd(top.Segment)} contributes **{top.SharePct.Value:0.##}%** of the total.");
        if (rows.Count >= 2)
        {
            var second = rows.OrderByDescending(r => r.Value).Skip(1).First();
            var gap = top.Value - second.Value;
            sb.AppendLine($"• Gap between the top two segments: **{FormatMetricValue(gap)}** {EscapeMd(metricLabel)}.");
        }
        sb.AppendLine();

        sb.AppendLine("| Segment | Value | Share % | Rank |");
        sb.AppendLine("| --- | ---: | ---: | ---: |");
        var rankCounter = 1;
        foreach (var row in topRows)
        {
            var share = row.SharePct.HasValue ? row.SharePct.Value.ToString("0.##") : "";
            var rank = row.Rank?.ToString() ?? rankCounter.ToString();
            sb.AppendLine($"| {EscapeMd(row.Segment)} | {FormatMetricValue(row.Value)} | {share} | {rank} |");
            rankCounter++;
        }

        if (rows.Count > topRows.Count)
            sb.AppendLine($"\nShowing first {topRows.Count} rows of {rows.Count} total segments.");

        return sb.ToString().Trim();
    }

    private async Task<string> CreateSegmentationChartAsync(string userText, List<SegmentationRow> rows)
    {
        var chartRows = rows
            .OrderByDescending(r => r.Value)
            .Take(12)
            .ToList();

        if (chartRows.Count == 0)
            throw new InvalidOperationException("No segmentation rows available for chart.");

        var labels = chartRows.Select(r => r.Segment).ToArray();
        var values = chartRows.Select(r => r.Value).ToArray();

        return await _chartTools.CreateBarChartPngAsync(
            GuessSegmentationChartTitle(userText),
            "Segment",
            GuessSegmentationMetricLabel(userText, rows),
            labels,
            values,
            width: 1000,
            height: 600);
    }

    private static string BuildSegmentationHiddenPayload(string userText, List<SegmentationRow> rows)
    {
        return JsonSerializer.Serialize(new
        {
            type = "segmentation_result",
            title = GuessSegmentationChartTitle(userText),
            metric = GuessSegmentationMetricLabel(userText, rows),
            segments = rows.Select(r => new
            {
                segment = r.Segment,
                value = r.Value,
                sharePct = r.SharePct,
                rank = r.Rank
            }).ToList()
        });
    }

    private static bool TryResolveSegmentLabel(Dictionary<string, JsonElement> map, out string segment)
    {
        foreach (var key in new[] { "Segment", "Category", "Group", "Series", "Label", "Name", "Type", "Status", "Region", "Territory", "Department" })
        {
            if (map.TryGetValue(key, out var el))
            {
                segment = el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.ToString();
                if (!string.IsNullOrWhiteSpace(segment))
                    return true;
            }
        }

        foreach (var kvp in map)
        {
            if (kvp.Value.ValueKind == JsonValueKind.String)
            {
                segment = kvp.Value.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(segment))
                    return true;
            }
        }

        segment = "";
        return false;
    }

    private static bool TryResolveNumericValue(Dictionary<string, JsonElement> map, out double value, out string valueColumn)
    {
        foreach (var key in new[] { "Value", "Amount", "Metric", "Total", "Count", "Sales", "Revenue", "Orders", "Headcount" })
        {
            if (map.TryGetValue(key, out var el) && TryGetDoubleValue(el, out value))
            {
                valueColumn = key;
                return true;
            }
        }

        foreach (var kvp in map)
        {
            if (TryGetDoubleValue(kvp.Value, out value))
            {
                valueColumn = kvp.Key;
                return true;
            }
        }

        value = 0;
        valueColumn = "Value";
        return false;
    }

    private static bool TryResolveOptionalNumeric(Dictionary<string, JsonElement> map, IEnumerable<string> keys, out double value)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var el) && TryGetDoubleValue(el, out value))
                return true;
        }

        value = 0;
        return false;
    }

    private static bool TryResolveOptionalInteger(Dictionary<string, JsonElement> map, IEnumerable<string> keys, out int value)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value))
                    return true;
                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out value))
                    return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryGetDoubleValue(JsonElement el, out double value)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value))
            return true;

        if (el.ValueKind == JsonValueKind.String)
        {
            var s = (el.GetString() ?? "").Replace(",", "").Replace("%", "").Trim();
            if (double.TryParse(s, out value))
                return true;
        }

        value = 0;
        return false;
    }

    private static string GuessSegmentationMetricLabel(string userText, List<SegmentationRow> rows)
    {
        var t = (userText ?? string.Empty).ToLowerInvariant();

        if (t.Contains("sales")) return "sales";
        if (t.Contains("revenue")) return "revenue";
        if (t.Contains("order")) return "orders";
        if (t.Contains("headcount") || t.Contains("employee")) return "headcount";
        if (t.Contains("cost")) return "cost";
        if (rows.Count > 0 && !string.IsNullOrWhiteSpace(rows[0].ValueColumn))
            return rows[0].ValueColumn;

        return "value";
    }
    private static string GuessSegmentationChartTitle(string userText)
    {
        var cleaned = Regex.Replace((userText ?? "Segmentation").Trim(), @"\s+", " ");
        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }

    private static string FormatMetricValue(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 0.0000001d)
            return Math.Round(value).ToString("0");

        return value.ToString("0.##");
    }

    private sealed record SegmentationRow(
        string Segment,
        double Value,
        double? SharePct,
        int? Rank,
        string ValueColumn);

    private async Task<string> CreateAnomalyChartAsync(AnomalyDetectionResult result)
    {
        if (!result.HasChart)
            throw new InvalidOperationException("No anomaly chart payload available.");

        if (result.SeriesNames.Count > 1)
        {
            return await _chartTools.CreateMultiSeriesLineChartPngAsync(
                result.ChartTitle,
                "Period",
                "Value",
                result.ChartLabels.ToArray(),
                result.SeriesNames.ToArray(),
                result.SeriesValues.ToArray(),
                width: 1000,
                height: 600);
        }

        return await _chartTools.CreateLineChartPngAsync(
            result.ChartTitle,
            "Period",
            "Value",
            result.ChartLabels.ToArray(),
            result.SeriesValues[0],
            width: 1000,
            height: 600);
    }

    private static string BuildMarkdownTable(IReadOnlyList<string> columns, IReadOnlyList<Dictionary<string, string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", columns.Select(EscapeMd)) + " |");
        sb.AppendLine("| " + string.Join(" | ", columns.Select(_ => "---")) + " |");
        foreach (var row in rows)
        {
            var cells = columns.Select(c => EscapeMd(row.TryGetValue(c, out var v) ? v : string.Empty));
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
        }
        return sb.ToString().Trim();
    }

    private static string BuildForecastSupportDataPrompt(string userText, ForecastRequest request, bool stricter = false)
    {
        var seriesLine = request.GroupBy is null
            ? "- Return rows with EXACT columns: Period, Value."
            : $"- Return rows with EXACT columns: Period, Value, Series. Series MUST contain the grouping label for '{request.GroupBy}'.";

        var stricterLine = stricter
            ? "- You previously asked a question. That is NOT allowed. Choose the most reasonable interpretation and proceed."
            : "- Do NOT ask follow-up questions. Choose the most reasonable interpretation and proceed.";

        return $@"
You are the SQL data exploration agent for forecasting support data.

{stricterLine}

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

MANDATORY OUTPUT RULES:
- Return ONLY the JSON returned by ExecuteSelectAsync.
- No markdown, no prose, no wrapping.
- Aggregate the data into a time series suitable for forecasting.
- Bucket by {request.Grain.ToString().ToLowerInvariant()}.
- Return historical data ordered by Period ascending.
{seriesLine}
- Period MUST be a date-like bucket label or ISO date string.
- Value MUST be numeric.
- Include enough history to support forecasting, preferably the last {request.HistoryPeriods} periods.

USER REQUEST:
{userText}
";
    }

    private static bool TryParseForecastInputPoints(string text, out List<ForecastInputPoint> points)
    {
        points = new List<ForecastInputPoint>();
        var json = ExtractFirstJsonObject(text) ?? (LooksLikeJson(text) ? text : null);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetPropIgnoreCase(doc.RootElement, "rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var row in rowsEl.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;

                if (!TryGetPropIgnoreCase(row, "Period", out var periodEl))
                    continue;
                if (!TryGetPropIgnoreCase(row, "Value", out var valueEl))
                    continue;

                var periodText = periodEl.ValueKind == JsonValueKind.String ? (periodEl.GetString() ?? "") : periodEl.ToString();
                if (!DateTime.TryParse(periodText, out var period))
                    continue;

                double value;
                if (valueEl.ValueKind == JsonValueKind.Number && valueEl.TryGetDouble(out var numeric))
                    value = numeric;
                else if (valueEl.ValueKind == JsonValueKind.String && double.TryParse(valueEl.GetString(), out var parsed))
                    value = parsed;
                else
                    continue;

                string? series = null;
                if (TryGetPropIgnoreCase(row, "Series", out var seriesEl))
                    series = seriesEl.ValueKind == JsonValueKind.String ? seriesEl.GetString() : seriesEl.ToString();

                points.Add(new ForecastInputPoint(period, value, string.IsNullOrWhiteSpace(series) ? null : series));
            }

            return points.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildForecastMarkdown(ForecastResult forecast)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Forecast Summary");
        sb.AppendLine(forecast.Summary);
        sb.AppendLine();

        if (forecast.Assumptions.Count > 0)
        {
            sb.AppendLine("Assumptions");
            foreach (var item in forecast.Assumptions)
                sb.AppendLine($"• {item}");
            sb.AppendLine();
        }

        var topRows = forecast.Points
            .OrderBy(p => p.Series ?? "")
            .ThenBy(p => p.Period)
            .Take(60)
            .ToList();

        sb.AppendLine("| Series | Period | Type | Value | Lower | Upper | Scenario |");
        sb.AppendLine("| --- | --- | --- | ---: | ---: | ---: | --- |");
        foreach (var p in topRows)
        {
            sb.AppendLine($"| {EscapeMd(p.Series ?? "Overall")} | {p.Period:yyyy-MM-dd} | {(p.IsForecast ? "Forecast" : "History")} | {p.Value:0.##} | {(p.LowerBound.HasValue ? p.LowerBound.Value.ToString("0.##") : "")} | {(p.UpperBound.HasValue ? p.UpperBound.Value.ToString("0.##") : "")} | {EscapeMd(p.Scenario ?? "")} |");
        }

        if (forecast.Points.Count > topRows.Count)
            sb.AppendLine($"\nShowing first {topRows.Count} rows of {forecast.Points.Count} total forecast rows.");

        return sb.ToString().Trim();
    }

    private async Task<string> CreateForecastChartAsync(ForecastResult forecast)
    {
        var forecastOnly = forecast.Points.Where(p => p.IsForecast).ToList();
        var history = forecast.Points.Where(p => !p.IsForecast).ToList();

        var allPeriods = forecast.Points.Select(p => p.Period).Distinct().OrderBy(d => d).ToList();
        var labels = allPeriods.Select(d => d.ToString("yyyy-MM-dd")).ToArray();

        if (forecast.Grouped)
        {
            var chosenSeries = forecast.Points
                .Select(p => p.Series ?? "Overall")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();

            var seriesNames = new List<string>();
            var seriesValues = new List<double[]>();

            foreach (var s in chosenSeries)
            {
                var values = allPeriods
                    .Select(period =>
                    {
                        var point = forecastOnly.FirstOrDefault(p => string.Equals(p.Series ?? "Overall", s, StringComparison.OrdinalIgnoreCase) && p.Period == period);
                        return point?.Value ?? double.NaN;
                    })
                    .Select(v => double.IsNaN(v) ? 0d : v)
                    .ToArray();

                if (values.Any(v => v != 0d))
                {
                    seriesNames.Add(s);
                    seriesValues.Add(values);
                }
            }

            if (seriesNames.Count == 0)
                throw new InvalidOperationException("No forecast series available for chart.");

            return await _chartTools.CreateMultiSeriesLineChartPngAsync(
                forecast.Title,
                "Period",
                yAxisLabel: "Value",
                xLabels: labels,
                seriesNames: seriesNames.ToArray(),
                seriesValues: seriesValues.ToArray(),
                width: 1000,
                height: 600);
        }
        else
        {
            var values = allPeriods
                .Select(period =>
                {
                    var hist = history.FirstOrDefault(p => p.Period == period && string.Equals(p.Scenario, "Baseline", StringComparison.OrdinalIgnoreCase));
                    if (hist is not null) return hist.Value;
                    var f = forecastOnly.FirstOrDefault(p => p.Period == period && string.Equals(p.Scenario, "Baseline", StringComparison.OrdinalIgnoreCase));
                    return f?.Value ?? 0d;
                })
                .ToArray();

            return await _chartTools.CreateLineChartPngAsync(
                forecast.Title,
                "Period",
                "Value",
                labels,
                values,
                width: 1000,
                height: 600);
        }
    }

    private static string BuildForecastHiddenPayload(ForecastResult forecast)
    {
        return JsonSerializer.Serialize(new
        {
            type = "forecast_result",
            title = forecast.Title,
            summary = forecast.Summary,
            grouped = forecast.Grouped,
            points = forecast.Points.Select(p => new
            {
                period = p.Period,
                series = p.Series,
                value = p.Value,
                lowerBound = p.LowerBound,
                upperBound = p.UpperBound,
                isForecast = p.IsForecast,
                scenario = p.Scenario
            }).ToList()
        });
    }




    private async Task<PipelineResult> ExecuteWhatIfSimulationAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        var request = WhatIfSimulationService.ParseRequest(userText);
        var supportPrompt = BuildWhatIfSupportDataPrompt(userText, request);

        var support = await _caller.CallAgentAsync(
            ChatAgentFactory.SqlAgentName,
            supportPrompt,
            cancellationToken: ct);

        var supportText = (support.Text ?? string.Empty).Trim();

        if (LooksLikeClarifyingQuestion(supportText))
        {
            var retryPrompt = BuildWhatIfSupportDataPrompt(userText, request, stricter: true);

            support = await _caller.CallAgentAsync(
                ChatAgentFactory.SqlAgentName,
                retryPrompt,
                cancellationToken: ct);

            supportText = (support.Text ?? string.Empty).Trim();
        }

        if (!WhatIfSimulationService.TryParseInputPoints(supportText, out var inputPoints))
        {
            var failText = "I couldn't run the what-if simulation because the supporting query did not return a usable baseline dataset. Please ask for a simulation on a numeric metric such as sales, revenue, cost, profit, orders, or headcount.";
            return await MaybeAttachDecisionCandidateAsync(userText, failText, ChatAgentFactory.WhatIfSimulationAgentName, routerReason, ct);
        }

        var simulation = WhatIfSimulationService.Run(request, inputPoints);
        var markdown = WhatIfSimulationService.BuildMarkdown(simulation);

        try
        {
            var chartUrl = await CreateWhatIfChartAsync(simulation);
            markdown = $"![chart]({chartUrl})\n\n" + markdown;
            markdown += WrapHiddenToolPayload("chart_result", JsonSerializer.Serialize(new { type = "chart_url", url = chartUrl }));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[WHATIF_CHART_FALLBACK] " + ex);
        }

        markdown += WrapHiddenToolPayload("whatif_result", WhatIfSimulationService.BuildHiddenPayload(simulation));

        var checkedAnswer = await RunPpiSafeAsync(userText, markdown, ct);

        return await MaybeAttachDecisionCandidateAsync(
            userText,
            checkedAnswer,
            ChatAgentFactory.WhatIfSimulationAgentName,
            routerReason + " (grounded via SqlAgent)",
            ct);
    }

    private static string BuildWhatIfSupportDataPrompt(string userText, WhatIfRequest request, bool stricter = false)
    {
        var strict = stricter
            ? "- You previously asked a question. That is NOT allowed. Choose the most reasonable interpretation and proceed."
            : "- Do NOT ask follow-up questions. Choose the most reasonable interpretation and proceed.";

        var groupingLine = string.IsNullOrWhiteSpace(request.GroupBy)
            ? "- Prefer a baseline time series if the schema supports it. Otherwise return the most relevant grouped baseline."
            : $"- If possible, group the returned baseline by '{request.GroupBy}' or the closest matching business dimension.";

        return $@"
You are the SQL agent for baseline what-if simulation support data.

NON-NEGOTIABLE RULES:
{strict}
- Use database tools only.
- Return a baseline dataset that can be used for deterministic what-if simulation.
- Prefer a single numeric metric and one clear label/dimension.
{groupingLine}
- Include enough rows to make the simulation meaningful, typically 8-24 rows for time series or 5-15 rows for grouped results.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

MANDATORY OUTPUT RULES:
- Return ONLY the JSON returned by ExecuteSelectAsync.
- No markdown, no prose, no wrapping.
- The returned rows MUST include:
  - Label: the baseline bucket label (for example month, territory, department, category, or other grouping label)
  - Value: the numeric baseline measure
  - Series: optional secondary grouping label if useful
- If exact aliases are not possible, still return one label-like column and one numeric measure column.
- Order the rows logically (time ascending when time-like, otherwise by relevance).

USER REQUEST:
{userText}

SIMULATION CONTEXT:
- Target metric: {request.MetricName}
- Changed variable: {request.VariableName}
- Scenario: {request.ScenarioName}
";
    }

    private async Task<string> CreateWhatIfChartAsync(WhatIfSimulationResult result)
    {
        var points = result.Points
            .OrderBy(p => p.Series ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.SortDate ?? DateTime.MaxValue)
            .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (points.Count == 0)
            throw new InvalidOperationException("No simulation rows available for chart.");

        if (result.Grouped)
        {
            var groups = points
                .GroupBy(p => p.Series ?? "Overall", StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();

            var labels = points
                .Where(p => string.Equals(p.Series ?? "Overall", groups[0].Key, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Label)
                .ToArray();

            var seriesNames = new List<string>();
            var seriesValues = new List<double[]>();

            foreach (var g in groups)
            {
                var values = labels
                    .Select(label => g.FirstOrDefault(p => string.Equals(p.Label, label, StringComparison.OrdinalIgnoreCase))?.ScenarioValue ?? 0d)
                    .ToArray();

                if (values.Any(v => v != 0d))
                {
                    seriesNames.Add(g.Key);
                    seriesValues.Add(values);
                }
            }

            if (result.TimeSeriesLike)
            {
                return await _chartTools.CreateMultiSeriesLineChartPngAsync(
                    result.Title,
                    "Label",
                    result.MetricLabel,
                    labels,
                    seriesNames.ToArray(),
                    seriesValues.ToArray(),
                    width: 1000,
                    height: 600);
            }

            return await _chartTools.CreateMultiSeriesColumnChartPngAsync(
                result.Title,
                "Label",
                result.MetricLabel,
                labels,
                seriesNames.ToArray(),
                seriesValues.ToArray(),
                width: 1000,
                height: 600);
        }

        var top = result.TimeSeriesLike
            ? points
            : points.OrderByDescending(p => Math.Abs(p.DeltaValue)).Take(12).ToList();

        var xLabels = top.Select(p => p.Label).ToArray();
        var scenarioValues = top.Select(p => p.ScenarioValue).ToArray();

        if (result.TimeSeriesLike)
        {
            return await _chartTools.CreateLineChartPngAsync(
                result.Title,
                "Label",
                result.MetricLabel,
                xLabels,
                scenarioValues,
                width: 1000,
                height: 600);
        }

        return await _chartTools.CreateBarChartPngAsync(
            result.Title,
            "Label",
            result.MetricLabel,
            xLabels,
            scenarioValues,
            width: 1000,
            height: 600);
    }

    private static string BuildDataIntelligenceSupportDataPrompt(string userText, bool stricter = false)
    {
        var strict = stricter
            ? "- You previously asked a question. That is NOT allowed. Choose the most reasonable interpretation and proceed."
            : "- Do NOT ask follow-up questions. Choose the most reasonable interpretation and proceed.";

        return $@"
You are the SQL data exploration agent for analytical support data.

NON-NEGOTIABLE RULES:
{strict}
- Use database tools only.
- Return the most relevant grounded dataset for the user's analysis request.
- Prefer compact, high-signal output over broad dumps.
- Choose the most business-relevant dimensions and metrics.
- When possible, return grouped or trend-oriented results that support interpretation.
- Limit output to a practical size unless the user explicitly asks for full detail.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

OUTPUT RULE (MANDATORY):
- You MUST return ONLY the JSON returned by ExecuteSelectAsync (SqlServerSelectTool).
- No markdown, no prose, no extra keys, no wrapping.

USER REQUEST:
{userText}
";
    }

    private static string PrepareDataIntelligenceSupportPayload(string userText, string supportText)
    {
        if (string.IsNullOrWhiteSpace(supportText))
        {
            return $$"""
            {
              "userRequest": {{JsonSerializer.Serialize(userText)}},
              "status": "no_support_data",
              "supportData": null
            }
            """;
        }

        var trimmed = supportText.Trim();

        if (string.Equals(trimmed, "unauthorized access", StringComparison.OrdinalIgnoreCase))
        {
            return $$"""
            {
              "userRequest": {{JsonSerializer.Serialize(userText)}},
              "status": "unauthorized",
              "supportData": null
            }
            """;
        }

        var json = ExtractFirstJsonObject(trimmed);
        if (!string.IsNullOrWhiteSpace(json))
        {
            return $$"""
            {
              "userRequest": {{JsonSerializer.Serialize(userText)}},
              "status": "ok",
              "supportData": {{json}}
            }
            """;
        }

        if (LooksLikeJson(trimmed))
        {
            return $$"""
            {
              "userRequest": {{JsonSerializer.Serialize(userText)}},
              "status": "ok",
              "supportData": {{trimmed}}
            }
            """;
        }

        return $$"""
        {
          "userRequest": {{JsonSerializer.Serialize(userText)}},
          "status": "unstructured_support",
          "supportText": {{JsonSerializer.Serialize(supportText)}}
        }
        """;
    }

    private static string BuildDataIntelligenceSynthesisPrompt(string userText, string supportPayload)
    {
        return $@"
You are producing a grounded data intelligence analysis.

Your task:
- Interpret the support payload.
- Explain the most meaningful signals in business terms.
- Highlight concentration, mix, change, ranking, imbalance, volatility, or operational concerns if present.
- Be explicit about uncertainty when the payload is narrow or incomplete.
- Do not invent calculations that are not directly supported.

Required markdown structure:

## Data Intelligence Summary
A concise summary of what matters most.

## What the data suggests
2-4 bullets.

## Key signals / patterns
2-5 bullets.

## Risks or watchouts
2-4 bullets.

## Recommended actions
2-4 bullets.

User request:
{userText}

Support payload:
{supportPayload}
";
    }

    private static string BuildExecutiveSupportDataPrompt(string userText, bool stricter = false)
    {
        var focus = InferExecutiveFocus(userText);

        var focusGuidance = focus switch
        {
            "sales" => @"
Preferred supporting datasets (pick the best 2-3 that the database can answer):
1. Overall sales KPI summary (for example total sales, order count, average order value if available).
2. Sales breakdown by territory / region / sales area (top 5-10).
3. Sales trend by month or by year-month for the latest available periods.
4. Top product categories or top products by sales if relevant.
",
            "customer" => @"
Preferred supporting datasets (pick the best 2-3 that the database can answer):
1. Customer count or customer segmentation summary.
2. Top customers by sales / order volume.
3. Customer purchasing trend over time.
4. Geographic or territory distribution of customers if relevant.
",
            "product" => @"
Preferred supporting datasets (pick the best 2-3 that the database can answer):
1. Top products or categories by sales / orders.
2. Product-category performance comparison.
3. Product sales trend over time if relevant.
",
            "workforce" => @"
Preferred supporting datasets (pick the best 2-3 that the database can answer):
1. Headcount summary.
2. Breakdown by department / title / gender / region if available.
3. Any visible concentration or imbalance supported by the data.
",
            _ => @"
Preferred supporting datasets (pick the best 2-3 that the database can answer):
1. One overall KPI summary relevant to the user request.
2. One grouped breakdown by the most important business dimension.
3. One time trend if the schema supports it.
"
        };

        var strict = stricter
            ? "You previously returned a clarifying question. That is NOT allowed. You MUST proceed with the most reasonable interpretation and return data now."
            : "You MUST NOT ask clarifying questions. If the request is broad, choose the most reasonable interpretation and proceed.";

        return $@"
You are gathering supporting facts for an executive summary from THIS application's database.

NON-NEGOTIABLE RULES:
- {strict}
- Use database tools only.
- Prefer compact, executive-relevant result sets.
- Limit grouped tables to about 5-10 rows.
- Limit trend tables to about 12 periods.
- Return ONLY strict JSON. No markdown. No prose. No code fences.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

BUSINESS FOCUS:
{focus}

{focusGuidance}

Return ONLY JSON in EXACTLY this schema:
{{
  ""focus"": ""{focus}"",
  ""summary"": ""<very short factual description>"",
  ""datasets"": [
    {{
      ""title"": ""<dataset title>"",
      ""columns"": [""Col1"", ""Col2""],
      ""rows"": [
        [""A"", ""1""],
        [""B"", ""2""]
      ]
    }}
  ]
}}

USER REQUEST:
{userText}
";
    }

    private static string PrepareExecutiveSupportPayload(string userText, string supportText)
    {
        if (string.IsNullOrWhiteSpace(supportText))
            return @"{""focus"":""unknown"",""summary"":""No supporting data returned."",""datasets"":[]}";

        var trimmed = supportText.Trim();

        if (string.Equals(trimmed, "unauthorized access", StringComparison.OrdinalIgnoreCase))
            return @"{""focus"":""unknown"",""summary"":""Database access was not authorized."",""datasets"":[]}";

        var json = ExtractFirstJsonObject(trimmed);
        if (!string.IsNullOrWhiteSpace(json))
            return TruncateForExecutiveInput(json);

        if (LooksLikeJson(trimmed))
            return TruncateForExecutiveInput(trimmed);

        if (TryRenderRowsObjectTableMarkdown(trimmed, out var mdFromRows))
        {
            return JsonSerializer.Serialize(new
            {
                focus = InferExecutiveFocus(userText),
                summary = "Supporting data returned as SQL rows.",
                datasets = new[]
                {
                    new
                    {
                        title = "SQL result",
                        columns = Array.Empty<string>(),
                        rows = Array.Empty<string[]>(),
                        markdown = mdFromRows
                    }
                }
            });
        }

        return JsonSerializer.Serialize(new
        {
            focus = InferExecutiveFocus(userText),
            summary = "Supporting data returned in text form.",
            datasets = new[]
            {
                new
                {
                    title = "Supporting result",
                    text = TruncateForExecutiveInput(trimmed)
                }
            }
        });
    }

    private static string BuildExecutiveSynthesisPrompt(string userText, string supportJson)
    {
        return $@"
Create a leadership-ready executive summary using ONLY the supporting data below.

Requirements:
- Be grounded in the data only.
- Do NOT invent numbers, trends, causes, or KPIs.
- If a cause is only a plausible interpretation, phrase it cautiously using terms like 'may' or 'could'.
- If the data is limited, say so clearly.
- Keep the tone concise, executive, and decision-oriented.

Use EXACTLY this structure:

Executive Summary
<2-3 sentence summary>

Key Insights
• Insight 1
• Insight 2
• Insight 3

Risks
• Risk 1
• Risk 2

Opportunities
• Opportunity 1
• Opportunity 2

Recommended Actions
• Action 1
• Action 2

Original user request:
{userText}

Supporting data:
{supportJson}
";
    }

    private static string InferExecutiveFocus(string? userText)
    {
        var t = (userText ?? string.Empty).ToLowerInvariant();

        if (t.Contains("customer"))
            return "customer";

        if (t.Contains("product") || t.Contains("category") || t.Contains("inventory"))
            return "product";

        if (t.Contains("employee") || t.Contains("headcount") || t.Contains("workforce") || t.Contains("department") || t.Contains("staff"))
            return "workforce";

        if (t.Contains("sales") || t.Contains("revenue") || t.Contains("order") || t.Contains("territory"))
            return "sales";

        return "business";
    }

    private static string TruncateForExecutiveInput(string text, int maxChars = 12000)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        return text.Length <= maxChars ? text : text[..maxChars];
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
