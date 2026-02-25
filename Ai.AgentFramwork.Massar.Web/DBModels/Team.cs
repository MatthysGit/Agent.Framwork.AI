using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class Team
{
    public int TeamId { get; set; }

    public string Name { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedOn { get; set; }

    public virtual ICollection<TeamRole> TeamRoles { get; set; } = new List<TeamRole>();
}
