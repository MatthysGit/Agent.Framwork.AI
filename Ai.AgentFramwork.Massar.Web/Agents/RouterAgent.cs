using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using System.Linq;
using System.Reflection;

namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class RouterAgent
{

    private readonly ChatClient _chat;
    private readonly Func<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>> _historyProvider;
    private readonly IAiUsageLogger? _aiUsageLogger;
    private readonly IAiUsageContextAccessor? _aiUsageContextAccessor;
    private readonly string? _modelKey;

    public RouterAgent(
        ChatClient chat,
        Func<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>> historyProvider,
        IAiUsageLogger? aiUsageLogger = null,
        IAiUsageContextAccessor? aiUsageContextAccessor = null,
        string? modelKey = null)
    {
        _chat = chat;
        _historyProvider = historyProvider;
        _aiUsageLogger = aiUsageLogger;
        _aiUsageContextAccessor = aiUsageContextAccessor;
        _modelKey = modelKey;
    }

    public sealed record RouteResult(
            string Mode,      // "agent" | "chart"
            string Agent,     // agent name
            string Reason,
            string? ChartType = null);

    public async Task<RouteResult> RouteAsync(string userMessage, CancellationToken ct = default)
    {
        // Hard guard: document rewrite intent must win over document edit intent.
        if (IsDocumentRewriteIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.DocumentRewriteAgentName,
                Reason: "Hard-guard: document rewrite request detected.");
        }

        // Hard guard: document editing intent.
        if (IsDocumentEditIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.DocumentEditAgentName,
                Reason: "Hard-guard: document edit intent detected.");
        }

        // Hard guard: spreadsheet analytics intent (must happen BEFORE document-content guard)
        if (IsExcelAnalyticsIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.ExcelAnalyticsAgentName,
                Reason: "Hard-guard: spreadsheet analytics intent detected.");
        }

        // Hard guard: document summary intent
        if (IsDocumentSummaryIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.DocumentSummaryAgentName,
                Reason: "Hard-guard: document summary request detected.");
        }

        // Hard guard: executive / leadership summary intent
        if (IsExecutiveInsightIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.ExecutiveInsightAgentName,
                Reason: "Hard-guard: executive insight request detected.");
        }

        // Hard guard: data intelligence / analytical interpretation intent
        if (IsDataIntelligenceIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.DataIntelligenceAgentName,
                Reason: "Hard-guard: data intelligence request detected.");
        }

        // Hard guard: what-if simulation / scenario analysis intent
        // IMPORTANT: must be before forecasting so scenario prompts do not get misrouted.
        if (IsWhatIfSimulationIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.WhatIfSimulationAgentName,
                Reason: "Hard-guard: what-if simulation request detected.");
        }

        // Hard guard: forecasting / predictive analytics intent
        if (IsForecastingIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.ForecastingAgentName,
                Reason: "Hard-guard: forecasting request detected.");
        }

        // Hard guard: anomaly detection / abnormality analysis intent
        if (IsAnomalyDetectionIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.AnomalyDetectionAgentName,
                Reason: "Hard-guard: anomaly detection request detected.");
        }

        // Hard guard: schema discovery belongs to explorer.
        if (IsSchemaDiscoveryIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.DataExplorerAgentName,
                Reason: "Hard-guard: schema discovery request detected.");
        }

        // Hard guard: profiling a specific table belongs to explorer, not direct SQL retrieval.
        if (IsTableProfilingIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.DataExplorerAgentName,
                Reason: "Hard-guard: table profiling/schema exploration request detected.");
        }

        // Hard guard: schema/entity relationship explanation belongs to explorer.
        if (IsSchemaRelationshipIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.DataExplorerAgentName,
                Reason: "Hard-guard: schema relationship exploration request detected.");
        }

        // Hard guard: explicit ranked / structured SQL retrieval intent
        // IMPORTANT: this must be before segmentation so territory ranking prompts do not get misrouted.
        if (IsExplicitSqlRetrievalIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.SqlAgentName,
                Reason: "Hard-guard: explicit SQL retrieval request detected.");
        }

        // Hard guard: open exploratory analysis intent
        // IMPORTANT: this must be before segmentation so exploration prompts do not get misrouted.
        if (IsOpenExplorationIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.DataExplorerAgentName,
                Reason: "Hard-guard: open exploratory analysis request detected.");
        }

        // Hard guard: segmentation / grouped breakdown intent
        if (IsSegmentationIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.DataSegmentationAgentName,
                Reason: "Hard-guard: segmentation request detected.");
        }

        // Hard guard: explicit document search / glossary / documentation lookup intent
        if (IsDocumentSearchIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.DocumentSearchAgentName,
                Reason: "Hard-guard: document search request detected.");
        }

        // Hard guard: "what is included / what does it say" doc questions
        if (IsDocumentContentQuestion(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.DocumentSearchAgentName,
                Reason: "Hard-guard: user is asking what is included / what does it say in a document (policy/contract/kit).");
        }

        // Safe short transcript (router uses it to match orchestrator behavior)
        var hist = _historyProvider?.Invoke() ?? Array.Empty<Microsoft.Extensions.AI.ChatMessage>();
        var last = hist.TakeLast(12)
            .Select(m => $"{m.Role}: {string.Concat(m.Contents.OfType<TextContent>().Select(t => t.Text))}")
            .Where(s => !string.IsNullOrWhiteSpace(s));

        var transcript = string.Join("\n", last);

        var system = $$$"""
You are the ROUTER only. Do NOT call tools. Do NOT answer the user.
Return ONLY strict JSON.

Schema (return exactly this shape):
{
  \"mode\": \"agent\" | \"chart\",
  \"agent\": \"{{{ChatAgentFactory.DataExplorerAgentName}}}\" 
        | \"{{{ChatAgentFactory.SqlAgentName}}}\" 
        | \"{{{ChatAgentFactory.DocumentSearchAgentName}}}\" 
        | \"{{{ChatAgentFactory.DocumentSummaryAgentName}}}\"
        | \"{{{ChatAgentFactory.DocumentRewriteAgentName}}}\"
        | \"{{{ChatAgentFactory.DocumentEditAgentName}}}\"
        | \"{{{ChatAgentFactory.ExcelAnalyticsAgentName}}}\"
        | \"{{{ChatAgentFactory.ExecutiveInsightAgentName}}}\"
        | \"{{{ChatAgentFactory.DataIntelligenceAgentName}}}\"
        | \"{{{ChatAgentFactory.ForecastingAgentName}}}\"
        | \"{{{ChatAgentFactory.WhatIfSimulationAgentName}}}\"
        | \"{{{ChatAgentFactory.AnomalyDetectionAgentName}}}\"
        | \"{{{ChatAgentFactory.DataSegmentationAgentName}}}\"
        | \"{{{ChatAgentFactory.LlmChatAgentName}}}\",
  \"reason\": \"<short reason>\",
  \"chartType\": \"column\"|\"bar\"|\"pie\"|\"line\"|\"area\"|\"donut\"|\"gauge\"|\"progress\"|\"multicolumn\"|null
}

ABSOLUTE DOCUMENT ROUTING RULES (MUST FOLLOW):

A) DOCUMENT EDIT INTENT (HIGHEST PRIORITY):
- If the user requests to edit/review/comment/annotate/highlight/suggest changes/track changes/rewrite/fix grammar/modify a document,
  choose agent \"{{{ChatAgentFactory.DocumentEditAgentName}}}\".

A2) EXCEL / SPREADSHEET ANALYTICS INTENT (HIGH PRIORITY, ONLY IF NOT EDITING):
- If the user asks to analyze a spreadsheet (insights, trends, KPIs, dashboard, anomalies, top items, financial/sales analysis),
  choose agent \"{{{ChatAgentFactory.ExcelAnalyticsAgentName}}}\".

A2.5) DOCUMENT SUMMARY INTENT:
- If the user asks to summarize a document/file/report/policy/contract/manual/meeting notes,
  or asks for executive summary, section summaries, action summary, or risks/decisions/next steps
  for a document, choose agent "{{{ChatAgentFactory.DocumentSummaryAgentName}}}".

A2.75) DOCUMENT REWRITE INTENT:
- If the user asks to rewrite/rephrase/reword a document or attached file in a different tone or style,
  including executive tone, formal business style, concise version, customer-friendly version, or board-ready version,
  choose agent "{{{ChatAgentFactory.DocumentRewriteAgentName}}}".
- Rewrite requests must go to DocumentRewriteAgent, not DocumentEditAgent.

A3) EXECUTIVE INSIGHT INTENT:
- If the user asks for executive summary, leadership summary, management summary, business overview,
  performance summary, top insights, key insights, what matters most, risks and opportunities,
  board summary, strategic insights, or an executive-level summary of performance,
  choose agent \"{{{ChatAgentFactory.ExecutiveInsightAgentName}}}\".

A4) DATA INTELLIGENCE INTENT:
- If the user asks for business intelligence summary, cross-domain insights, most important insights,
  recommended actions for leadership, or interpretation across products, territories, and customers,
  choose agent \"{{{ChatAgentFactory.DataIntelligenceAgentName}}}\".

A5) WHAT-IF / SCENARIO SIMULATION INTENT:
- If the user asks what happens if a business variable changes, simulate impact, or run a what-if scenario,
  choose agent \"{{{ChatAgentFactory.WhatIfSimulationAgentName}}}\".
- Examples:
  - \"What happens if we increase prices by 10%?\"
  - \"Simulate a 5% reduction in cost by department\"
  - \"What if headcount grows 8% next year?\"

A6) FORECASTING / PREDICTIVE ANALYTICS INTENT:
- If the user asks to forecast, predict, project forward, or estimate future values,
  choose agent \"{{{ChatAgentFactory.ForecastingAgentName}}}\".
- Examples:
  - \"Forecast monthly sales for the next 6 months\"
  - \"Predict revenue by region next quarter\"
  - \"Project orders for the next 12 months\"

A7) ANOMALY DETECTION INTENT:
- If the user asks to identify anomalies, unusual spikes/drops, outliers, abnormal patterns, or unexpected changes,
  choose agent \"{{{ChatAgentFactory.AnomalyDetectionAgentName}}}\".

A8) OPEN EXPLORATORY ANALYSIS INTENT:
- If the user asks to explore data broadly, find main patterns, strongest/weakest markets, notable trends,
  across geography/category/region/country/product dimensions, choose agent \"{{{ChatAgentFactory.DataExplorerAgentName}}}\".

A9) SEGMENTATION INTENT:
- If the user asks to segment customers/entities based on spend, frequency, recency, value bands, cohorts, or segment labels,
  choose agent \"{{{ChatAgentFactory.DataSegmentationAgentName}}}\".

B) DOCUMENT SEARCH / LOOKUP INTENT:
- If the user's message OR recent chat context indicates they want information contained in a document (policy/contract/agreement/kit),
  choose agent \"{{{ChatAgentFactory.DocumentSearchAgentName}}}\".

CHART RULE:
- If user asks for a chart/plot/graph AND it requires ANY SQL/database query:
  mode=\"chart\", agent=\"{{{ChatAgentFactory.SqlAgentName}}}\", chartType best fit.

SQL RULE:
- If the user asks for explicit ranked / tabular / direct retrieval such as top N, ordered results, territory rankings,
  or explicitly asks to show rows/columns from structured sales tables, choose agent \"{{{ChatAgentFactory.SqlAgentName}}}\".
- Open exploratory analysis should go to \"{{{ChatAgentFactory.DataExplorerAgentName}}}\" instead.

FALLBACK:
- Otherwise => agent \"{{{ChatAgentFactory.LlmChatAgentName}}}\".

Return ONLY JSON. No markdown. No extra keys.
""";

        var messages = new List<OpenAI.Chat.ChatMessage>
    {
        new SystemChatMessage(system),
        new UserChatMessage($"""
Recent transcript:
{transcript}

User message:
{userMessage}
""")
    };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await _chat.CompleteChatAsync(messages, cancellationToken: ct);
        sw.Stop();
        var text = resp.Value?.Content?.FirstOrDefault()?.Text?.Trim() ?? "";

        if (_aiUsageLogger is not null)
        {
            var ctx = _aiUsageContextAccessor?.GetCurrent();
            _aiUsageContextAccessor?.SetAgent(ChatAgentFactory.OrchestratorAgentName, _modelKey);
            var snapshot = AiUsageReflection.ExtractSnapshot(resp);
            var estimatedPrompt = snapshot.PromptTokens ?? AiTokenEstimator.EstimateTextTokens(system) + AiTokenEstimator.EstimateTextTokens(transcript) + AiTokenEstimator.EstimateTextTokens(userMessage) + 24;
            var estimatedCompletion = snapshot.CompletionTokens ?? AiTokenEstimator.EstimateTextTokens(text);

            await _aiUsageLogger.LogAsync(new AiUsageLogRequest(
                AgentName: ChatAgentFactory.OrchestratorAgentName,
                ModelName: _modelKey ?? ctx?.ModelName ?? "unknown",
                UserId: ctx?.UserId,
                ConversationId: ctx?.ConversationId,
                MessageId: ctx?.MessageId,
                PromptTokens: snapshot.PromptTokens ?? estimatedPrompt,
                CompletionTokens: snapshot.CompletionTokens ?? estimatedCompletion,
                TotalTokens: snapshot.TotalTokens ?? (estimatedPrompt + estimatedCompletion),
                CachedInputTokens: snapshot.CachedInputTokens,
                ReasoningTokens: snapshot.ReasoningTokens,
                RequestId: snapshot.RequestId,
                ClientRequestId: ctx?.ClientRequestId,
                LatencyMs: (int)sw.ElapsedMilliseconds,
                Succeeded: true,
                ErrorMessage: null), ct);
        }

        try
        {
            var decision = JsonSerializer.Deserialize<RouteResult>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (decision is null || string.IsNullOrWhiteSpace(decision.Mode) || string.IsNullOrWhiteSpace(decision.Agent))
                return new RouteResult("agent", ChatAgentFactory.LlmChatAgentName, "Empty router decision; fallback.");

            var mode = (decision.Mode ?? "agent").Trim().ToLowerInvariant();
            var agent = (decision.Agent ?? ChatAgentFactory.LlmChatAgentName).Trim();

            if (mode != "agent" && mode != "chart")
                mode = "agent";

            // Final hard-guards (in case model output contradicts intent)
            if (IsDocumentRewriteIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.DocumentRewriteAgentName, "Hard-guard: document rewrite request detected.");

            if (IsDocumentEditIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.DocumentEditAgentName, "Hard-guard: document edit intent detected.");

            if (IsExcelAnalyticsIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.ExcelAnalyticsAgentName, "Hard-guard: spreadsheet analytics intent detected.");

            if (IsDocumentSummaryIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.DocumentSummaryAgentName, "Hard-guard: document summary request detected.");

            if (IsExecutiveInsightIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.ExecutiveInsightAgentName, "Hard-guard: executive insight request detected.");

            if (IsDataIntelligenceIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.DataIntelligenceAgentName, "Hard-guard: data intelligence request detected.");

            if (IsWhatIfSimulationIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.WhatIfSimulationAgentName, "Hard-guard: what-if simulation request detected.");

            if (IsForecastingIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.ForecastingAgentName, "Hard-guard: forecasting request detected.");

            if (IsAnomalyDetectionIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.AnomalyDetectionAgentName, "Hard-guard: anomaly detection request detected.");

            if (IsSchemaDiscoveryIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.DataExplorerAgentName, "Hard-guard: schema discovery request detected.");

            if (IsExplicitSqlRetrievalIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.SqlAgentName, "Hard-guard: explicit SQL retrieval request detected.");

            if (IsOpenExplorationIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.DataExplorerAgentName, "Hard-guard: open exploratory analysis request detected.");

            if (IsSegmentationIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.DataSegmentationAgentName, "Hard-guard: segmentation request detected.");

            if (IsDocumentSearchIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.DocumentSearchAgentName, "Hard-guard: document search request detected.");

            if (IsDocumentContentQuestion(userMessage))
                return new RouteResult("agent", ChatAgentFactory.DocumentSearchAgentName, "Hard-guard: document content question detected.");

            return decision with { Mode = mode, Agent = agent };
        }
        catch
        {
            return new RouteResult("agent", ChatAgentFactory.LlmChatAgentName, "Router output not valid JSON; fallback.");
        }
    }


    private static bool IsDocumentSummaryIntent(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (IsExcelAnalyticsIntent(message) || IsDocumentRewriteIntent(message))
            return false;

        var t = message.Trim().ToLowerInvariant();

        var summaryCue =
            t.Contains("summarize") ||
            t.Contains("summary") ||
            t.Contains("executive summary") ||
            t.Contains("section summary") ||
            t.Contains("section summaries") ||
            t.Contains("action summary") ||
            t.Contains("next steps") ||
            t.Contains("risks") ||
            t.Contains("decisions") ||
            t.Contains("key decisions") ||
            t.Contains("document summary") ||
            t.Contains("summarise");

        if (!summaryCue)
            return false;

        var docCue =
            t.Contains("document") ||
            t.Contains("file") ||
            t.Contains("report") ||
            t.Contains("policy") ||
            t.Contains("contract") ||
            t.Contains("agreement") ||
            t.Contains("proposal") ||
            t.Contains("manual") ||
            t.Contains("meeting notes") ||
            t.Contains("minutes") ||
            t.Contains("memo") ||
            t.Contains("doc") ||
            t.Contains("pdf") ||
            t.Contains("word");

        return docCue;
    }

    private static bool IsDocumentRewriteIntent(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (IsExcelAnalyticsIntent(message))
            return false;

        var t = message.Trim().ToLowerInvariant();

        var rewriteCue =
            t.Contains("rewrite") ||
            t.Contains("rephrase") ||
            t.Contains("reword") ||
            t.Contains("polish") ||
            t.Contains("formal business style") ||
            t.Contains("formal style") ||
            t.Contains("concise version") ||
            t.Contains("make it concise") ||
            t.Contains("customer-friendly") ||
            t.Contains("customer friendly") ||
            t.Contains("board-ready") ||
            t.Contains("board ready") ||
            t.Contains("executive tone") ||
            t.Contains("formal tone") ||
            t.Contains("rewrite this") ||
            t.Contains("rewrite the document") ||
            t.Contains("rewrite the report") ||
            t.Contains("rewrite this in") ||
            t.Contains("make this more formal") ||
            t.Contains("make it more formal") ||
            t.Contains("make this customer friendly") ||
            t.Contains("make this customer-friendly") ||
            t.Contains("make this board ready") ||
            t.Contains("make this board-ready");

        if (!rewriteCue)
            return false;

        var styleOnlyCue =
            t.Contains("formal business style") ||
            t.Contains("formal style") ||
            t.Contains("concise version") ||
            t.Contains("make it concise") ||
            t.Contains("customer-friendly") ||
            t.Contains("customer friendly") ||
            t.Contains("board-ready") ||
            t.Contains("board ready") ||
            t.Contains("executive tone") ||
            t.Contains("formal tone") ||
            t.Contains("rewrite this in") ||
            t.Contains("make this more formal") ||
            t.Contains("make it more formal") ||
            t.Contains("make this customer friendly") ||
            t.Contains("make this customer-friendly") ||
            t.Contains("make this board ready") ||
            t.Contains("make this board-ready");

        if (styleOnlyCue)
            return true;

        var docCue =
            t.Contains("document") ||
            t.Contains("file") ||
            t.Contains("report") ||
            t.Contains("policy") ||
            t.Contains("contract") ||
            t.Contains("agreement") ||
            t.Contains("proposal") ||
            t.Contains("manual") ||
            t.Contains("meeting notes") ||
            t.Contains("minutes") ||
            t.Contains("memo") ||
            t.Contains("doc") ||
            t.Contains("pdf") ||
            t.Contains("word") ||
            t.Contains("summary");

        return docCue;
    }

    private static bool IsExplicitSqlRetrievalIntent(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (IsSchemaDiscoveryIntent(message) || IsTableProfilingIntent(message) || IsSchemaRelationshipIntent(message))
            return false;

        if (IsDocumentSearchIntent(message) || IsForecastingIntent(message) || IsWhatIfSimulationIntent(message))
            return false;

        var text = message.Trim();
        var t = text.ToLowerInvariant();

        var explicitSqlLanguage =
            t.Contains("write sql") ||
            t.Contains("generate sql") ||
            t.Contains("show sql") ||
            t.Contains("sql query") ||
            t.Contains("select statement") ||
            t.Contains("t-sql") ||
            t.Contains("explain the sql") ||
            t.Contains("explain sql") ||
            t.Contains("query sales from table") ||
            t.StartsWith("query ") ||
            t.StartsWith("select ") ||
            t.StartsWith("delete ") ||
            t.StartsWith("update ") ||
            t.StartsWith("insert ") ||
            t.StartsWith("drop ") ||
            t.StartsWith("truncate ");

        var rankedRetrieval =
            t.Contains("top 10") ||
            t.Contains("top 5") ||
            t.Contains("top ") ||
            t.Contains("rank ") ||
            t.Contains("ranked") ||
            t.Contains("ordered results") ||
            t.Contains("order by");

        var sqlShapedRequest =
            t.Contains("with territory name") ||
            t.Contains("territory name") ||
            t.Contains("customer name") ||
            t.Contains("product category") ||
            t.Contains("by year") ||
            t.Contains("table ") ||
            t.Contains("rows") ||
            t.Contains("columns") ||
            t.Contains("show me the query") ||
            System.Text.RegularExpressions.Regex.IsMatch(t, @"\b[a-z_][a-z0-9_]*\.[a-z_][a-z0-9_]*\b");

        return
            explicitSqlLanguage ||
            (rankedRetrieval && sqlShapedRequest) ||
            text.Contains("top sales territories", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("sales territory", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("salesytd", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("saleslastyear", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("country region code", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSchemaDiscoveryIntent(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var t = message.Trim().ToLowerInvariant();

        return
            t.Contains("what tables should i use") ||
            t.Contains("which tables should i use") ||
            t.StartsWith("what tables") ||
            t.StartsWith("which tables") ||
            t.Contains("what columns") ||
            t.Contains("which columns") ||
            t.Contains("where can i find") ||
            t.Contains("what schema") ||
            t.Contains("which schema") ||
            t.Contains("how do i join") ||
            t.Contains("how should i join") ||
            t.Contains("what joins") ||
            t.Contains("what data source") ||
            t.Contains("which data source") ||
            t.Contains("how is data stored") ||
            t.Contains("where is the data") ||
            t.Contains("which table contains") ||
            t.Contains("what table contains");
    }

    private static bool IsTableProfilingIntent(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var t = message.Trim().ToLowerInvariant();

        var profilingVerb =
            t.StartsWith("profile ") ||
            t.StartsWith("describe ") ||
            t.StartsWith("inspect ") ||
            t.StartsWith("explore ") ||
            t.Contains(" table profile") ||
            t.Contains("profile table") ||
            t.Contains("describe table") ||
            t.Contains("inspect table") ||
            t.Contains("explain table") ||
            t.Contains("list columns") ||
            t.Contains("show columns") ||
            t.Contains("what columns are in");

        var referencesSpecificTable =
            System.Text.RegularExpressions.Regex.IsMatch(t, @"\b[a-z_][a-z0-9_]*\.[a-z_][a-z0-9_]*\b") ||
            t.Contains(" table ") ||
            t.EndsWith(" table") ||
            t.StartsWith("table ");

        return profilingVerb && referencesSpecificTable;
    }

    private static bool IsSchemaRelationshipIntent(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (IsDataIntelligenceIntent(message))
            return false;

        var t = message.Trim().ToLowerInvariant();

        var relationshipLanguage =
            t.StartsWith("explain how ") ||
            t.Contains(" relate to ") ||
            t.Contains(" relates to ") ||
            t.Contains(" relationship between ") ||
            (t.Contains("how are ") && t.Contains(" related")) ||
            (t.Contains("how do ") && t.Contains(" relate")) ||
            (t.Contains("how does ") && t.Contains(" relate")) ||
            (t.Contains("how are ") && t.Contains(" connected")) ||
            (t.Contains("how do ") && t.Contains(" connect"));

        var schemaTerms =
            t.Contains("table") ||
            t.Contains("tables") ||
            t.Contains("column") ||
            t.Contains("columns") ||
            t.Contains("schema") ||
            t.Contains("join") ||
            t.Contains("joins") ||
            t.Contains("foreign key") ||
            t.Contains("relationship") ||
            t.Contains("relationships") ||
            t.Contains("data model") ||
            t.Contains("entity") ||
            t.Contains("employee") ||
            t.Contains("employees") ||
            System.Text.RegularExpressions.Regex.IsMatch(t, @"\b[a-z_][a-z0-9_]*\.[a-z_][a-z0-9_]*\b");

        return relationshipLanguage && schemaTerms;
    }

    private static bool IsOpenExplorationIntent(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var text = message.Trim();

        return
            text.Contains("explore reseller sales", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("show the main patterns", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("strongest markets", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("weakest markets", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("notable trends", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("by geography and category", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("by country, region, and product category", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("explore sales performance", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDocumentSearchIntent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (IsExcelAnalyticsIntent(text) || IsDocumentEditIntent(text))
            return false;

        var t = text.ToLowerInvariant();

        var seeksDocumentation =
            t.Contains("find documentation") ||
            t.Contains("find docs") ||
            t.Contains("find documents") ||
            t.Contains("project documents") ||
            t.Contains("our project documents") ||
            t.Contains("business glossary") ||
            t.Contains("official definition") ||
            t.Contains("definition of") ||
            t.Contains("documentation about") ||
            t.Contains("docs related to") ||
            t.Contains("find documentation about") ||
            t.Contains("find docs related to") ||
            t.Contains("in our project documents") ||
            t.Contains("in the project documents") ||
            t.Contains("in our documents") ||
            t.Contains("according to the glossary") ||
            t.Contains("what is the definition of") ||
            t.Contains("what's the definition of");

        var documentConcept =
            t.Contains("documentation") ||
            t.Contains("docs") ||
            t.Contains("documents") ||
            t.Contains("glossary") ||
            t.Contains("definition") ||
            t.Contains("project document") ||
            t.Contains("project documents");

        var glossaryStyleQuestion =
            (t.Contains("official definition") || t.Contains("definition of") || t.Contains("what is the definition of") || t.Contains("what's the definition of")) &&
            (t.Contains("document") || t.Contains("documents") || t.Contains("docs") || t.Contains("glossary") || t.Contains("project"));

        return seeksDocumentation
               || glossaryStyleQuestion
               || (documentConcept && (t.Contains("find") || t.Contains("what is the official definition") || t.Contains("what's the official definition")));
    }

    private static bool IsDocumentContentQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Important: do NOT treat spreadsheet analytics or executive-summary requests as document-search
        if (IsExcelAnalyticsIntent(text) || IsExecutiveInsightIntent(text) || IsDataIntelligenceIntent(text))
            return false;

        var t = text.ToLowerInvariant();

        // Content-seeking phrasing
        var asksWhatsInside =
            t.Contains("what is included") ||
            t.Contains("what's included") ||
            t.Contains("what is in the") ||
            t.Contains("what's in the") ||
            t.Contains("what is inside") ||
            t.Contains("what does it say") ||
            t.Contains("what does the") ||
            t.Contains("what is covered") ||
            t.Contains("summarize") ||
            t.Contains("according to") ||
            t.Contains("from the document") ||
            t.Contains("in the document") ||
            t.Contains("in the pdf") ||
            t.Contains("quote") ||
            t.Contains("cite");

        // Document nouns (very important for your examples)
        var docNoun =
            t.Contains("document") ||
            t.Contains("pdf") ||
            t.Contains("policy") ||
            t.Contains("contract") ||
            t.Contains("agreement") ||
            t.Contains("survival kit") ||
            t.Contains("kit");

        // Links / attachment markers (keep, but spreadsheet analytics and executive summaries are excluded above)
        var hasLinkMarkers =
            t.Contains("/api/chat/attachments/") ||
            t.Contains("/documents/files/download/");

        // If it clearly asks about contents and references a doc-ish noun, route to doc search
        if ((asksWhatsInside && docNoun) || hasLinkMarkers)
            return true;

        // Also route if the question is literally about "policy" or "contract" contents even without "document" keyword
        if (docNoun && (t.Contains("what") || t.Contains("summarize") || t.Contains("according to")))
            return true;

        return false;
    }

    private static bool IsDocumentEditIntent(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (IsDocumentRewriteIntent(message))
            return false;

        var text = message.Trim();

        var hasEditVerb =
            text.Contains("comment on", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("add comments", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("annotate", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("highlight", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("proofread", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("revise", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("edit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("review", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("track changes", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("suggest changes", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("fix grammar", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("modify", StringComparison.OrdinalIgnoreCase);

        var hasDocumentContext =
            text.Contains("document", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("doc", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("docx", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("pdf", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("file", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("contract", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("agreement", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("policy", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("proposal", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("attachment", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("/api/chat/attachments/", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("/documents/files/download/", StringComparison.OrdinalIgnoreCase);

        var hasStrongDocumentEditPhrase =
            text.Contains("review the attached", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("comment on the document", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("add comments to the document", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("annotate this document", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("proofread this contract", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("revise this document", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("edit this agreement", StringComparison.OrdinalIgnoreCase);

        return hasStrongDocumentEditPhrase || (hasEditVerb && hasDocumentContext);
    }
    private static bool IsExcelAnalyticsIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.ToLowerInvariant();

        // Spreadsheet hints
        var mentionsSpreadsheet =
            t.Contains("excel") || t.Contains("spreadsheet") || t.Contains("worksheet") ||
            t.Contains("xlsx") || t.Contains("xls") || t.Contains("csv") ||
            t.Contains("/api/chat/attachments/") || t.Contains("/documents/files/download/");

        // Analytics intent
        var analytics =
            t.Contains("analy") ||            // analyze/analysis/analytics
            t.Contains("insight") ||
            t.Contains("kpi") ||
            t.Contains("dashboard") ||
            t.Contains("trend") ||
            t.Contains("forecast") ||
            t.Contains("outlier") ||
            t.Contains("anomal") ||           // anomaly/anomalies
            t.Contains("top ") ||
            t.Contains("breakdown") ||
            t.Contains("group") ||
            t.Contains("pivot") ||
            t.Contains("performance") ||
            t.Contains("profit") ||
            t.Contains("revenue") ||
            t.Contains("sales") ||
            t.Contains("cost") ||
            t.Contains("margin");

        // Chart intent paired with spreadsheet context
        var wantsChart =
            t.Contains("chart") || t.Contains("graph") || t.Contains("plot") || t.Contains("visual");

        // If they clearly want analytics and it's likely spreadsheet-related => ExcelAnalyticsAgent
        if (mentionsSpreadsheet && (analytics || wantsChart))
            return true;

        // Even without explicit spreadsheet keyword, if they say "analyze the attached file/spreadsheet"
        if ((t.Contains("analyze") || t.Contains("insights") || t.Contains("dashboard")) &&
            (t.Contains("attached") || t.Contains("attachment") || t.Contains("file")))
            return true;

        return false;
    }

    private static bool IsForecastingIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (IsExcelAnalyticsIntent(text) || IsWhatIfSimulationIntent(text))
            return false;

        var t = text.ToLowerInvariant();

        var mentionsForecast =
            t.Contains("forecast") ||
            t.Contains("predict") ||
            t.Contains("projection") ||
            t.Contains("project forward") ||
            t.Contains("projected") ||
            t.Contains("predictive") ||
            t.Contains("outlook");

        var mentionsFutureWindow =
            t.Contains("next month") || t.Contains("next quarter") || t.Contains("next year") ||
            t.Contains("next 3") || t.Contains("next 6") || t.Contains("next 12") ||
            t.Contains("future") || t.Contains("upcoming");

        var mentionsHistoricalModeling =
            t.Contains("based on prior years") ||
            t.Contains("based on previous years") ||
            t.Contains("seasonality") ||
            t.Contains("seasonal") ||
            t.Contains("holiday-season") ||
            t.Contains("holiday season") ||
            t.Contains("launched last week");

        var businessMetric =
            t.Contains("sales") || t.Contains("revenue") || t.Contains("order") || t.Contains("demand") ||
            t.Contains("volume") || t.Contains("cost") || t.Contains("margin") || t.Contains("profit");

        return (mentionsForecast && businessMetric) ||
               (mentionsFutureWindow && businessMetric) ||
               (mentionsHistoricalModeling && (mentionsForecast || businessMetric));
    }

    private static bool IsAnomalyDetectionIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (IsExcelAnalyticsIntent(text) || IsForecastingIntent(text))
            return false;

        var t = text.ToLowerInvariant();

        var mentionsAnomaly =
            t.Contains("anomaly") ||
            t.Contains("anomalies") ||
            t.Contains("outlier") ||
            t.Contains("outliers") ||
            t.Contains("abnormal") ||
            t.Contains("unusual") ||
            t.Contains("unexpected") ||
            t.Contains("spike") ||
            t.Contains("drop") ||
            t.Contains("structural break") ||
            t.Contains("change point") ||
            t.Contains("deviation");

        var businessMetric =
            t.Contains("sales") || t.Contains("revenue") || t.Contains("order") || t.Contains("demand") ||
            t.Contains("volume") || t.Contains("cost") || t.Contains("margin") || t.Contains("profit") ||
            t.Contains("headcount") || t.Contains("employee") || t.Contains("performance");

        return mentionsAnomaly && (businessMetric || t.Contains("trend") || t.Contains("time series") || t.Contains("metric"));
    }

    private static bool IsSegmentationIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (IsExcelAnalyticsIntent(text) || IsForecastingIntent(text) || IsAnomalyDetectionIntent(text) || IsExecutiveInsightIntent(text) || IsDataIntelligenceIntent(text))
            return false;

        var t = text.ToLowerInvariant();

        var businessMetric =
            t.Contains("sales") || t.Contains("revenue") || t.Contains("order") || t.Contains("orders") ||
            t.Contains("profit") || t.Contains("margin") || t.Contains("cost") || t.Contains("expense") ||
            t.Contains("employee") || t.Contains("employees") || t.Contains("headcount") || t.Contains("customer") ||
            t.Contains("customers") || t.Contains("invoice") || t.Contains("invoices") || t.Contains("amount") ||
            t.Contains("count") || t.Contains("total");

        var segmentationLanguage =
            t.Contains("segment") ||
            t.Contains("segmentation") ||
            t.Contains("high-value") ||
            t.Contains("high value") ||
            t.Contains("medium value") ||
            t.Contains("low value") ||
            t.Contains("bucket") ||
            t.Contains("classify") ||
            t.Contains("cohort");

        return businessMetric && segmentationLanguage;
    }



    private static bool IsWhatIfSimulationIntent(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var text = message.Trim();

        return
            text.Contains("what if", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("what-if", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("simulate", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("scenario", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("impact if", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("what would be the impact", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("impact on total revenue", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("impact on gross profit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("baseline vs simulated", StringComparison.OrdinalIgnoreCase) ||
            (
                text.Contains("increase", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("discount", StringComparison.OrdinalIgnoreCase)
            ) ||
            (
                text.Contains("sales volume", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("discount rate", StringComparison.OrdinalIgnoreCase)
            ) ||
            (
                text.Contains("increased by", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("while", StringComparison.OrdinalIgnoreCase)
            );
    }
    private static bool IsDataIntelligenceIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (IsExcelAnalyticsIntent(text) || IsExecutiveInsightIntent(text))
            return false;

        var t = text.ToLowerInvariant();

        return t.Contains("data intelligence")
               || t.Contains("business intelligence summary")
               || t.Contains("what does the data suggest")
               || t.Contains("what do the data suggest")
               || t.Contains("analytical interpretation")
               || t.Contains("analyze the signals")
               || t.Contains("key signals")
               || t.Contains("patterns in the data")
               || t.Contains("drivers of performance")
               || t.Contains("business signals")
               || t.Contains("intelligence view")
               || t.Contains("intelligence analysis")
               || t.Contains("why did")
               || t.Contains("exact reason")
               || t.Contains("identify products where sales are strong but")
               || t.Contains("procurement or stock patterns may be risky")
               || t.Contains("most important sales insights")
               || (t.Contains("what does") && t.Contains("data suggest"))
               || (t.Contains("what do") && t.Contains("data suggest"));
    }

    private static bool IsExecutiveInsightIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.ToLowerInvariant();

        return t.Contains("executive summary")
               || t.Contains("leadership summary")
               || t.Contains("management summary")
               || t.Contains("business overview")
               || t.Contains("performance summary")
               || t.Contains("top insights")
               || t.Contains("key insights")
               || t.Contains("what matters most")
               || t.Contains("risks and opportunities")
               || t.Contains("board summary")
               || t.Contains("strategic insights")
               || t.Contains("executive-level summary")
               || t.Contains("summary for leadership")
               || t.Contains("summary for management")
               || t.Contains("leadership should know")
               || t.Contains("management should know")
               || t.Contains("leadership review")
               || t.Contains("coo worry about")
               || t.Contains("for a cfo audience");
    }
}
