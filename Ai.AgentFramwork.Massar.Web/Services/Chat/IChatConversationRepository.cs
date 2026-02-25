using Ai.AgentFramwork.Massar.Web.DTO;
using Microsoft.Extensions.AI;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

public interface IChatConversationRepository
{
    Task<Guid> EnsureConversationAsync(string userId, string? title, CancellationToken ct = default);

    Task<Guid> AppendMessageAsync(
        Guid conversationId,
        string senderRole,
        string content,
        string contentType,
        string? metadataJson,
        IEnumerable<ChatAttachmentInfo>? attachments,
        CancellationToken ct = default);

    Task<List<ConversationListItem>> GetUserConversationsAsync(
        string userId,
        bool includeArchived = false,
        int take = 50,
        CancellationToken ct = default);

    Task<(List<ChatMessage> Messages, Guid ConversationId)> LoadConversationAsync(
        string userId,
        Guid conversationId,
        Func<string, IEnumerable<ChatAttachmentInfo>, string> appendAttachmentLinks,
        CancellationToken ct = default);

    Task RenameConversationAsync(string userId, Guid conversationId, string? newTitle, CancellationToken ct = default);
    Task SetArchiveStateAsync(string userId, Guid conversationId, bool isArchived, CancellationToken ct = default);
    Task DeleteConversationAsync(string userId, Guid conversationId, CancellationToken ct = default);
}
