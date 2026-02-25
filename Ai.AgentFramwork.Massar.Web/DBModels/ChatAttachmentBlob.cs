using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class ChatAttachmentBlob
{
    public Guid AttachmentId { get; set; }

    public string FileName { get; set; } = null!;

    public string ContentType { get; set; } = null!;

    public long FileSizeBytes { get; set; }

    public byte[] FileContent { get; set; } = null!;

    public DateTime CreatedUtc { get; set; }
}
