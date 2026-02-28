using System.Text.Json;
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
        // include a safe short transcript so router matches orchestrator behavior
        var hist = _historyProvider?.Invoke() ?? Array.Empty<Microsoft.Extensions.AI.ChatMessage>();
        var last = hist.TakeLast(12)
            .Select(m => $"{m.Role}: {string.Concat(m.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text))}")
            .Where(s => !string.IsNullOrWhiteSpace(s));

        var transcript = string.Join("\n", last);

        var system = $"""
You are the ROUTER only. Do NOT call tools. Do NOT answer the user.
Return ONLY strict JSON.

Schema:
{{
  "mode": "agent" | "chart",
  "agent": "{ChatAgentFactory.SqlAgentName}" | "{ChatAgentFactory.DocumentSearchAgentName}" | "{ChatAgentFactory.DocumentEditAgentName}" | "{ChatAgentFactory.LlmChatAgentName}",
  "reason": "<short reason>",
  "chartType": "bar"|"pie"|"line"|"area"|"donut"|"gauge"|"progress"|"multicolumn"|null
}}

ABSOLUTE DOCUMENT ROUTING RULES (MUST FOLLOW):

A) DOCUMENT EDIT INTENT (HIGHEST PRIORITY):
- If the user requests to edit/review/comment/annotate/highlight/suggest changes/track changes/rewrite/fix grammar/modify a document,
  choose agent "{ChatAgentFactory.DocumentEditAgentName}".

B) DOCUMENT SEARCH / LOOKUP INTENT (ONLY IF NOT EDITING):
- If the user's message OR recent chat context contains ANY of:
  - '/api/chat/attachments/' or '/documents/files/download/'
  - pdf, doc, docx, txt, csv, xls, xlsx
  - 'according to', 'in the document', 'in the pdf', 'from the file', 'what does it say', 'summarize', 'quote', 'cite'
  choose agent "{ChatAgentFactory.DocumentSearchAgentName}".

CHART RULE:
- If user asks for a chart/plot/graph AND it requires ANY SQL/database query:
  mode="chart", agent="{ChatAgentFactory.SqlAgentName}", chartType best fit.

SQL RULE:
- If clearly SQL/database (not chart) => agent "{ChatAgentFactory.SqlAgentName}".

FALLBACK:
- Otherwise => agent "{ChatAgentFactory.LlmChatAgentName}".

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

            return decision;
        }
        catch
        {
            return new RouteResult("agent", ChatAgentFactory.LlmChatAgentName, "Router output not valid JSON; fallback.");
        }
    }
}