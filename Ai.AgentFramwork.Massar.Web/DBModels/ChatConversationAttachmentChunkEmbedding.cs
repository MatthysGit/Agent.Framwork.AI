using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class ChatConversationAttachmentChunkEmbedding
{
    public Guid ChatConversationAttachmentChunkId { get; set; }

    public string EmbeddingModel { get; set; } = null!;

    public int Dimensions { get; set; }

    public byte[] VectorBinary { get; set; } = null!;

    public DateTime CreatedUtc { get; set; }

    public virtual ChatConversationAttachmentChunk ChatConversationAttachmentChunk { get; set; } = null!;
}
