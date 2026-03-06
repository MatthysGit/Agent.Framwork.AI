using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using System.Linq;

namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class RouterAgent
{

    private readonly ChatClient _chat;
    private readonly Func<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>> _historyProvider;

    public RouterAgent(ChatClient chat, Func<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>> historyProvider)
    {
        _chat = chat;
        _historyProvider = historyProvider;
    }

    public sealed record RouteResult(
            string Mode,      // "agent" | "chart"
            string Agent,     // agent name
            string Reason,
            string? ChartType = null);

    public async Task<RouteResult> RouteAsync(string userMessage, CancellationToken ct = default)
    {
        // Hard guard: document editing intent always wins.
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

        // Hard guard: executive / leadership summary intent
        if (IsExecutiveInsightIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.ExecutiveInsightAgentName,
                Reason: "Hard-guard: executive insight request detected.");
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

        // IMPORTANT: use $$$""" so JSON braces are treated as literal content
        var system = $$$"""
You are the ROUTER only. Do NOT call tools. Do NOT answer the user.
Return ONLY strict JSON.

Schema (return exactly this shape):
{
  \"mode\": \"agent\" | \"chart\",
  \"agent\": \"{{{ChatAgentFactory.DataExplorerAgentName}}}\" 
        | \"{{{ChatAgentFactory.SqlAgentName}}}\" 
        | \"{{{ChatAgentFactory.DocumentSearchAgentName}}}\" 
        | \"{{{ChatAgentFactory.DocumentEditAgentName}}}\"
        | \"{{{ChatAgentFactory.ExcelAnalyticsAgentName}}}\"
        | \"{{{ChatAgentFactory.ExecutiveInsightAgentName}}}\"
        | \"{{{ChatAgentFactory.ForecastingAgentName}}}\"
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
- This is true even if the message mentions xlsx/xls/csv or contains an attachment link.
- Examples:
  - \"Analyze this spreadsheet\"
  - \"Generate insights from the attached Excel\"
  - \"Create a dashboard / KPIs\"
  - \"Find top categories and a trend chart\"
  - \"Summarize sales performance and plot revenue over time\"

A3) EXECUTIVE INSIGHT INTENT:
- If the user asks for executive summary, leadership summary, management summary, business overview,
  performance summary, top insights, key insights, what matters most, risks and opportunities,
  board summary, strategic insights, or an executive-level summary of performance,
  choose agent \"{{{ChatAgentFactory.ExecutiveInsightAgentName}}}\".
- Prefer this route when the user wants leadership-ready interpretation rather than raw data rows.
- Examples:
  - \"Give me an executive summary of company performance this month\"
  - \"What are the top insights leadership should know?\"
  - \"Summarize the business risks and opportunities\"
  - \"What matters most for management this week?\"

A4) FORECASTING / PREDICTIVE ANALYTICS INTENT:
- If the user asks to forecast, predict, project forward, estimate future values, or generate baseline / optimistic / conservative scenarios,
  choose agent \"{{{ChatAgentFactory.ForecastingAgentName}}}\".
- Examples:
  - \"Forecast monthly sales for the next 6 months\"
  - \"Predict revenue by region next quarter\"
  - \"Project orders for the next 12 months\"

B) DOCUMENT SEARCH / LOOKUP INTENT (ONLY IF NOT EDITING OR EXCEL ANALYTICS OR EXECUTIVE INSIGHT):
- If the user's message OR recent chat context indicates they want information contained in a document (policy/contract/agreement/kit),
  you MUST choose agent \"{{{ChatAgentFactory.DocumentSearchAgentName}}}\".
  This includes questions like:
  - \"what is included in the survival kit\"
  - \"what is included in the policy\"
  - \"what does the contract say\"
  - \"what is in the agreement\"
  - \"what does the document say about ...\"
  - \"summarize the policy/contract\"
  - \"according to the document/policy/contract ...\"
- Also choose \"{{{ChatAgentFactory.DocumentSearchAgentName}}}\" if the message OR recent chat context contains ANY of:
  - '/api/chat/attachments/' or '/documents/files/download/'
  - pdf, doc, docx, txt
  - 'according to', 'in the document', 'in the pdf', 'from the file', 'what does it say', 'summarize', 'quote', 'cite'
NOTE:
- Do NOT route to DocumentSearchAgent just because of csv/xls/xlsx if the intent is analytics/insights/KPIs/dashboard/charts.

CHART RULE:
- If user asks for a chart/plot/graph AND it requires ANY SQL/database query:
  mode=\"chart\", agent=\"{{{ChatAgentFactory.SqlAgentName}}}\", chartType best fit.

SQL RULE:
- If clearly SQL/database (not chart) => agent \"{{{ChatAgentFactory.DataExplorerAgentName}}}\".

SQL / DATA EXPLORATION INTENT KEYWORDS (ROUTE TO DataExplorer):
- If the user asks for: count / how many / number of / total / sum / avg / average / min / max
- Or asks for: breakdown / grouped by / per / by <dimension>
- Or mentions database/query/select/sql/table/view
- Or asks about business metrics like: employees, headcount, sales, revenue, orders, invoices, territories
=> choose agent \"{{{ChatAgentFactory.DataExplorerAgentName}}}\" (mode=\"agent\" unless chart requested).

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

        var resp = await _chat.CompleteChatAsync(messages, cancellationToken: ct);
        var text = resp.Value?.Content?.FirstOrDefault()?.Text?.Trim() ?? "";

        try
        {
            var decision = JsonSerializer.Deserialize<RouteResult>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (decision is null || string.IsNullOrWhiteSpace(decision.Mode) || string.IsNullOrWhiteSpace(decision.Agent))
                return new RouteResult("agent", ChatAgentFactory.LlmChatAgentName, "Empty router decision; fallback.");

            // Safety normalization
            var mode = (decision.Mode ?? "agent").Trim().ToLowerInvariant();
            var agent = (decision.Agent ?? ChatAgentFactory.LlmChatAgentName).Trim();

            if (mode != "agent" && mode != "chart")
                mode = "agent";

            // Final hard-guards (in case model output contradicts intent)
            if (IsDocumentEditIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.DocumentEditAgentName, "Hard-guard: document edit intent detected.");

            if (IsExcelAnalyticsIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.ExcelAnalyticsAgentName, "Hard-guard: spreadsheet analytics intent detected.");

            if (IsExecutiveInsightIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.ExecutiveInsightAgentName, "Hard-guard: executive insight request detected.");

            if (IsForecastingIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.ForecastingAgentName, "Hard-guard: forecasting request detected.");

            if (IsAnomalyDetectionIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.AnomalyDetectionAgentName, "Hard-guard: anomaly detection request detected.");

            if (IsDocumentContentQuestion(userMessage))
                return new RouteResult("agent", ChatAgentFactory.DocumentSearchAgentName, "Hard-guard: document content question detected.");

            return decision with { Mode = mode, Agent = agent };
        }
        catch
        {
            return new RouteResult("agent", ChatAgentFactory.LlmChatAgentName, "Router output not valid JSON; fallback.");
        }
    }

    private static bool IsDocumentContentQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Important: do NOT treat spreadsheet analytics or executive-summary requests as document-search
        if (IsExcelAnalyticsIntent(text) || IsExecutiveInsightIntent(text))
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

    private static bool IsDocumentEditIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.ToLowerInvariant();

        return t.Contains("add comment")
               || t.Contains("add comments")
               || t.Contains("comment on")
               || t.Contains("review the document")
               || t.Contains("review this document")
               || t.Contains("review")
               || t.Contains("annotate")
               || t.Contains("highlight")
               || t.Contains("track changes")
               || t.Contains("suggest changes")
               || t.Contains("edit the document")
               || t.Contains("proofread")
               || t.Contains("revise")
               || t.Contains("rewrite")
               || t.Contains("fix grammar")
               || t.Contains("make changes");
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

        if (IsExcelAnalyticsIntent(text))
            return false;

        var t = text.ToLowerInvariant();

        var mentionsForecast =
            t.Contains("forecast") ||
            t.Contains("predict") ||
            t.Contains("projection") ||
            t.Contains("project ") ||
            t.Contains("projected") ||
            t.Contains("predictive") ||
            t.Contains("expected ") ||
            t.Contains("outlook") ||
            t.Contains("scenario");

        var mentionsFutureWindow =
            t.Contains("next month") || t.Contains("next quarter") || t.Contains("next year") ||
            t.Contains("next 3") || t.Contains("next 6") || t.Contains("next 12") ||
            t.Contains("future") || t.Contains("upcoming");

        var businessMetric =
            t.Contains("sales") || t.Contains("revenue") || t.Contains("order") || t.Contains("demand") ||
            t.Contains("volume") || t.Contains("cost") || t.Contains("margin") || t.Contains("profit");

        return mentionsForecast || (mentionsFutureWindow && businessMetric);
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
               || t.Contains("management should know");
    }
}
