namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

public interface IChatAttachmentBlobReader
{
    Task<(byte[] Bytes, string FileName, string ContentType)> ReadAsync(Guid attachmentId, CancellationToken ct = default);
}
