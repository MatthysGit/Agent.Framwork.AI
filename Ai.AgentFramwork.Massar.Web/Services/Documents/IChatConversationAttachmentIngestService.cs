namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public interface IChatConversationAttachmentIngestService
{
    Task IngestAsync(
        Guid conversationId,
        Guid attachmentId,
        string fileName,
        string contentType,
        byte[] bytes,
        CancellationToken ct = default);
}