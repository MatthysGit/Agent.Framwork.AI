using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class ChatMessageAttachment
{
    public int ChatMessageAttachmentId { get; set; }

    public Guid AttachmentId { get; set; }

    public Guid MessageId { get; set; }

    public string FileName { get; set; } = null!;

    public string? MimeType { get; set; }

    public long SizeBytes { get; set; }

    public string? StoredPath { get; set; }

    public string? StorageUrl { get; set; }

    public bool IsFromAgent { get; set; }

    public DateTime CreatedUtc { get; set; }

    public virtual ChatMessage Message { get; set; } = null!;
}
