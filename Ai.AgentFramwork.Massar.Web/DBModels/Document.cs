using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class Document
{
    public Guid DocumentId { get; set; }

    public string SourceName { get; set; } = null!;

    public string? OriginalFileName { get; set; }

    public byte[]? ContentHash { get; set; }

    public DateTime CreatedUtc { get; set; }

    public virtual ICollection<DocumentCategoryMap> DocumentCategoryMaps { get; set; } = new List<DocumentCategoryMap>();

    public virtual ICollection<DocumentChunk> DocumentChunks { get; set; } = new List<DocumentChunk>();

    public virtual ICollection<DocumentFile> DocumentFiles { get; set; } = new List<DocumentFile>();

    public virtual ICollection<DocumentMetadatum> DocumentMetadata { get; set; } = new List<DocumentMetadatum>();

    public virtual ICollection<DocumentRoleAccess> DocumentRoleAccesses { get; set; } = new List<DocumentRoleAccess>();
}
