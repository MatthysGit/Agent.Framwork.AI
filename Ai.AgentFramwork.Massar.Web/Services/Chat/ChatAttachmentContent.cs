namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

public sealed record ChatAttachmentContent(
    Guid AttachmentId,
    string FileName,
    string? MimeType,
    long SizeBytes,
    string DownloadUrl
);  
