namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

public readonly record struct ChatAttachmentInfo(
    Guid AttachmentId,
    string FileName,
    string MimeType,
    string StorageUrl,
    long SizeBytes);
