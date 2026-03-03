using System.Text;
using System.Text.Json;
using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.DTO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

public interface IImproveConversationService
{
    Task<ImproveConversationResultDto> ImproveAsync(string userId, Guid conversationId, ImproveOptions? options = null, CancellationToken ct = default);
}

public sealed record ImproveOptions(
    string Tone = "neutral",         // neutral | executive | friendly
    string Detail = "standard",      // short | standard | detailed
    bool NoQuestions = true
);

public sealed class ImproveConversationService : IImproveConversationService
{
    private readonly IChatConversationRepository _repo;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IConfiguration _cfg;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ImproveConversationService(
        IChatConversationRepository repo,
        IChatClientFactory chatClientFactory,
        IConfiguration cfg)
    {
        _repo = repo;
        _chatClientFactory = chatClientFactory;
        _cfg = cfg;
    }

    public async Task<ImproveConversationResultDto> ImproveAsync(
        string userId,
        Guid conversationId,
        ImproveOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new ImproveOptions();

        var (msgs, _) = await _repo.LoadConversationAsync(
            userId,
            conversationId,
            appendAttachmentLinks: (t, _atts) => t,
            ct: ct);

        var transcript = BuildTranscript(msgs);

        var system = """
You are a productivity assistant.

Return ONLY strict JSON matching this schema exactly:

{
  "thread_summary": "string",
  "key_decisions": [ { "decision": "string", "context": "string|null", "owners": ["string"] } ],
  "action_items": [ { "task": "string", "owner": "string|null", "due_date": "string|null", "priority": "P1|P2|P3|null", "status": "todo|in_progress|done|null" } ],
  "open_questions": [ { "question": "string", "blocked_action_refs": ["string"] } ],
  "artifacts": {
    "email": { "subject": "string", "to": ["string"], "cc": ["string"], "body_markdown": "string" },
    "spec": {
      "title": "string",
      "overview": "string",
      "goals": ["string"],
      "non_goals": ["string"],
      "requirements": [ { "id": "REQ-1", "text": "string", "priority": "Must|Should|Could" } ],
      "acceptance_criteria": ["string"],
      "risks": ["string"],
      "assumptions": ["string"]
    }
  }
}

Rules:
- Only treat something as a decision if it is explicitly agreed/confirmed in the thread.
- Action items must be verb-first and atomic.
- Never invent owners/dates/emails. If missing, set owner/due_date to null and add an open_question or assumption.
- Keep email “to/cc” empty arrays if recipients are not explicitly stated.
- Email body must be markdown and include: summary, decisions, action items, open questions.
""";

        var user = $"""
Preferences:
- tone: {options.Tone}
- detail: {options.Detail}
- no_questions: {options.NoQuestions}

Conversation transcript:
{transcript}
""";

        var modelKey = _cfg["OpenAI:ChatModel"] ?? throw new InvalidOperationException("OpenAI:ChatModel not configured.");
        ChatClient client = _chatClientFactory.Create(modelKey);

        var completion = await client.CompleteChatAsync(
            new OpenAI.Chat.ChatMessage[]
            {
                new SystemChatMessage(system),
                new UserChatMessage(user)
            },
            new ChatCompletionOptions
            {
                Temperature = 0.2f
            },
            ct);

        var raw = completion.Value?.Content?.FirstOrDefault()?.Text ?? "";
        var json = ExtractFirstJson(raw) ?? raw;

        var dto = JsonSerializer.Deserialize<ImproveConversationResultDto>(json, JsonOpts);
        if (dto is null)
            throw new InvalidOperationException("ImproveConversation: model returned invalid JSON.");

        return dto;
    }

    private static string BuildTranscript(List<Microsoft.Extensions.AI.ChatMessage> msgs)
    {
        var sb = new StringBuilder();
        foreach (var m in msgs)
        {
            var role = m.Role.ToString().ToUpperInvariant();
            var text = string.Concat(m.Contents.OfType<TextContent>().Select(t => t.Text)).Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            sb.AppendLine($"[{role}] {text}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string? ExtractFirstJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var s = text.Trim()
            .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
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
}