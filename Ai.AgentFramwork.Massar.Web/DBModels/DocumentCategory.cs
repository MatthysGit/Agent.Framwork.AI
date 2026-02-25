using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class DocumentCategory
{
    public int DocumentCategoryId { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedUtc { get; set; }

    public virtual ICollection<DocumentCategoryMap> DocumentCategoryMaps { get; set; } = new List<DocumentCategoryMap>();
}
