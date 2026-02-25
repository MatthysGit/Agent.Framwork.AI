using Microsoft.EntityFrameworkCore;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

[Keyless]
public class ChatAttachmentBlobRow
{
    public Guid AttachmentId { get; set; }
    public string FileName { get; set; } = "";
    public string? ContentType { get; set; }
    public long FileSizeBytes { get; set; }
    public byte[] FileContent { get; set; } = Array.Empty<byte>();
    public DateTime CreatedUtc { get; set; }
}