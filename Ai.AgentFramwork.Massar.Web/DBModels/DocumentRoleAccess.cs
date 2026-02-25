using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class DocumentRoleAccess
{
    public long DocumentRoleAccessId { get; set; }

    public Guid DocumentId { get; set; }

    public int RoleId { get; set; }

    public bool CanRead { get; set; }

    public bool CanDownload { get; set; }

    public bool CanManage { get; set; }

    public bool IsActive { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public DateTime ModifiedUtc { get; set; }

    public virtual Document Document { get; set; } = null!;

    public virtual Role Role { get; set; } = null!;
}
