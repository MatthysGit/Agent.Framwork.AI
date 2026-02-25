using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class UserTeamRole
{
    public string UserId { get; set; } = null!;

    public int TeamRoleId { get; set; }

    public DateTime AddedAtUtc { get; set; }

    public virtual TeamRole TeamRole { get; set; } = null!;

    public virtual AppUser User { get; set; } = null!;
}
