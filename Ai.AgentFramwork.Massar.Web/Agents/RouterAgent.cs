using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Ai.AgentFramwork.Massar.Web.Services.Chat;

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
        // Hard guard: if this is clearly a "what is included / what does it say" doc question,
        // always route to DocumentSearchAgent (unless it is editing intent).
        if (IsDocumentEditIntent(userMessage))
        {
            return new RouteResult(
                Mode: "agent",
                Agent: ChatAgentFactory.DocumentEditAgentName,
                Reason: "Hard-guard: document edit intent detected.");
        }

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

        // IMPORTANT: use $$""" so JSON braces are treated as literal content
        var system = $$"""
You are the ROUTER only. Do NOT call tools. Do NOT answer the user.
Return ONLY strict JSON.

Schema (return exactly this shape):
{
  "mode": "agent" | "chart",
  "agent": "{{ChatAgentFactory.SqlAgentName}}" | "{{ChatAgentFactory.DocumentSearchAgentName}}" | "{{ChatAgentFactory.DocumentEditAgentName}}" | "{{ChatAgentFactory.LlmChatAgentName}}",
  "reason": "<short reason>",
  "chartType": "bar"|"pie"|"line"|"area"|"donut"|"gauge"|"progress"|"multicolumn"|null
}

ABSOLUTE DOCUMENT ROUTING RULES (MUST FOLLOW):

A) DOCUMENT EDIT INTENT (HIGHEST PRIORITY):
- If the user requests to edit/review/comment/annotate/highlight/suggest changes/track changes/rewrite/fix grammar/modify a document,
  choose agent "{{ChatAgentFactory.DocumentEditAgentName}}".

B) DOCUMENT SEARCH / LOOKUP INTENT (ONLY IF NOT EDITING):
- If the user's message OR recent chat context indicates they want information contained in a document (policy/contract/agreement/kit),
  you MUST choose agent "{{ChatAgentFactory.DocumentSearchAgentName}}".
  This includes questions like:
  - "what is included in the survival kit"
  - "what is included in the policy"
  - "what does the contract say"
  - "what is in the agreement"
  - "what does the document say about ..."
  - "summarize the policy/contract"
  - "according to the document/policy/contract ..."
- Also choose "{{ChatAgentFactory.DocumentSearchAgentName}}" if the message OR recent chat context contains ANY of:
  - '/api/chat/attachments/' or '/documents/files/download/'
  - pdf, doc, docx, txt, csv, xls, xlsx
  - 'according to', 'in the document', 'in the pdf', 'from the file', 'what does it say', 'summarize', 'quote', 'cite'

CHART RULE:
- If user asks for a chart/plot/graph AND it requires ANY SQL/database query:
  mode="chart", agent="{{ChatAgentFactory.SqlAgentName}}", chartType best fit.

SQL RULE:
- If clearly SQL/database (not chart) => agent "{{ChatAgentFactory.SqlAgentName}}".

FALLBACK:
- Otherwise => agent "{{ChatAgentFactory.LlmChatAgentName}}".

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

            // Final hard-guard (in case model output contradicts intent)
            if (IsDocumentEditIntent(userMessage))
                return new RouteResult("agent", ChatAgentFactory.DocumentEditAgentName, "Hard-guard: document edit intent detected.");

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

        // Links / attachment markers
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
}