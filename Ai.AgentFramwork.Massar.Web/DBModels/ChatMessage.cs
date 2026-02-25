using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class ChatMessage
{
    public Guid MessageId { get; set; }

    public Guid ConversationId { get; set; }

    public string SenderRole { get; set; } = null!;

    public int SequenceNo { get; set; }

    public string ContentType { get; set; } = null!;

    public string? Content { get; set; }

    public string? MetadataJson { get; set; }

    public DateTime CreatedUtc { get; set; }

    public virtual ICollection<ChatMessageAttachment> ChatMessageAttachments { get; set; } = new List<ChatMessageAttachment>();

    public virtual ChatConversation Conversation { get; set; } = null!;
}
