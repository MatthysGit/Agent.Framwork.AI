using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class ChatConversation
{
    public Guid ConversationId { get; set; }

    public string UserId { get; set; } = null!;

    public string? Title { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public bool IsArchived { get; set; }

    public virtual ICollection<ChatConversationAttachmentIngest> ChatConversationAttachmentIngests { get; set; } = new List<ChatConversationAttachmentIngest>();

    public virtual ICollection<ChatMessage> ChatMessages { get; set; } = new List<ChatMessage>();

    public virtual AppUser User { get; set; } = null!;
}
