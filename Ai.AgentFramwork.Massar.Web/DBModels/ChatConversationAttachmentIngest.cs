using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class ChatConversationAttachmentIngest
{
    public Guid ChatConversationAttachmentIngestId { get; set; }

    public Guid ConversationId { get; set; }

    public Guid AttachmentId { get; set; }

    public string FileName { get; set; } = null!;

    public string ContentType { get; set; } = null!;

    public long FileSizeBytes { get; set; }

    public byte[] ContentHash { get; set; } = null!;

    public bool UsedOcr { get; set; }

    public string? ExtractedText { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public virtual ICollection<ChatConversationAttachmentChunk> ChatConversationAttachmentChunks { get; set; } = new List<ChatConversationAttachmentChunk>();

    public virtual ChatConversation Conversation { get; set; } = null!;
}
