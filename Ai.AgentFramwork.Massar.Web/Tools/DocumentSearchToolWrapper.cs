using Ai.AgentFramwork.Massar.Web.Tools;
using OpenAI.Chat;
using System.ComponentModel;
using Ai.AgentFramwork.Massar.Web.Services.Chat;

namespace Ai.AgentFramwork.Massar.Web.Tools;

public sealed class DocumentSearchToolWrapper
{
    private readonly DocumentSearchTool _inner;
    private readonly ChatSession _session;

    public DocumentSearchToolWrapper(DocumentSearchTool inner, ChatSession session)
    {
        _inner = inner;
        _session = session;
    }

    [Description("Search documents for a conversation. conversationId MUST be a GUID string. Returns JSON.")]
    public Task<string> SearchDocumentsAsync(
        [Description("Conversation Id as GUID string")] string conversationId,
        [Description("Search query")] string query)
    {
        // NOTE: Your original code overwrote the argument unconditionally.
        // This keeps the intended behavior: prefer passed-in id if valid, else fallback.
        if (!Guid.TryParse(conversationId, out var convoId))
        {
            var fallback = _session.ActiveConversationId?.ToString();
            if (string.IsNullOrWhiteSpace(fallback) || !Guid.TryParse(fallback, out convoId))
                return Task.FromResult(@"{""Text"":""Invalid conversation id."",""Attachments"":[]}");
        }

        return _inner.SearchDocumentsAsync(convoId, query);
    }
}