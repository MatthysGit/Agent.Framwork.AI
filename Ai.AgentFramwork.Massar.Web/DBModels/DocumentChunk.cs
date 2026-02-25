using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class DocumentChunk
{
    public Guid ChunkId { get; set; }

    public Guid DocumentId { get; set; }

    public int ChunkIndex { get; set; }

    public int? PageNumber { get; set; }

    public string Text { get; set; } = null!;

    public DateTime CreatedUtc { get; set; }

    public virtual ICollection<ChunkEmbedding> ChunkEmbeddings { get; set; } = new List<ChunkEmbedding>();

    public virtual Document Document { get; set; } = null!;
}
