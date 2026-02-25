using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class Role
{
    public int RoleId { get; set; }

    public string Name { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedOn { get; set; }

    public virtual ICollection<AppUser> AppUsers { get; set; } = new List<AppUser>();

    public virtual ICollection<DocumentRoleAccess> DocumentRoleAccesses { get; set; } = new List<DocumentRoleAccess>();

    public virtual ICollection<TableFieldDefinitionRoleAccess> TableFieldDefinitionRoleAccesses { get; set; } = new List<TableFieldDefinitionRoleAccess>();

    public virtual ICollection<TeamRole> TeamRoles { get; set; } = new List<TeamRole>();
}
