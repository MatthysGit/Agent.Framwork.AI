using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class ChatAttachmentIngest
{
    public Guid ChatAttachmentIngestId { get; set; }

    public Guid ConversationId { get; set; }

    public Guid ChatAttachmentBlobId { get; set; }

    public string FileName { get; set; } = null!;

    public string? ContentType { get; set; }

    public byte[] ContentHash { get; set; } = null!;

    public string? ExtractedText { get; set; }

    public bool UsedOcr { get; set; }

    public DateTime ExtractedUtc { get; set; }
}
