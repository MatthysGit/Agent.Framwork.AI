using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class UserSecurityGroup
{
    public string UserId { get; set; } = null!;

    public int SecurityGroupId { get; set; }

    public DateTime AddedAtUtc { get; set; }

    public virtual AppUser User { get; set; } = null!;
}
