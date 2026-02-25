using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class ChatConversationAttachmentChunk
{
    public Guid ChatConversationAttachmentChunkId { get; set; }

    public Guid ChatConversationAttachmentIngestId { get; set; }

    public int ChunkIndex { get; set; }

    public string Text { get; set; } = null!;

    public DateTime CreatedUtc { get; set; }

    public virtual ICollection<ChatConversationAttachmentChunkEmbedding> ChatConversationAttachmentChunkEmbeddings { get; set; } = new List<ChatConversationAttachmentChunkEmbedding>();

    public virtual ChatConversationAttachmentIngest ChatConversationAttachmentIngest { get; set; } = null!;
}
