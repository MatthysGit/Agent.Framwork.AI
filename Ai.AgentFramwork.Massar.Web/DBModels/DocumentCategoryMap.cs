using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class DocumentCategoryMap
{
    public Guid DocumentId { get; set; }

    public int DocumentCategoryId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public virtual Document Document { get; set; } = null!;

    public virtual DocumentCategory DocumentCategory { get; set; } = null!;
}
