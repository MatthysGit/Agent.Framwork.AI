using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class TeamRole
{
    public int TeamRoleId { get; set; }

    public int TeamId { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedOn { get; set; }

    public int RoleId { get; set; }

    public virtual Role Role { get; set; } = null!;

    public virtual Team Team { get; set; } = null!;

    public virtual ICollection<UserTeamRole> UserTeamRoles { get; set; } = new List<UserTeamRole>();
}
