using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class ChatAttachmentContent
{
    public Guid AttachmentId { get; set; }

    public string FileName { get; set; } = null!;

    public string ContentType { get; set; } = null!;

    public byte[] Content { get; set; } = null!;

    public DateTime CreatedUtc { get; set; }
}
