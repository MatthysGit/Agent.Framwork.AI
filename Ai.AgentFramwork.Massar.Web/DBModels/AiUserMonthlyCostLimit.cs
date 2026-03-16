using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class AiUserMonthlyCostLimit
{
    public string UserId { get; set; } = null!;

    public decimal MonthlyLimitUsd { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedOn { get; set; }

    public DateTime UpdatedOn { get; set; }

    public virtual AppUser User { get; set; } = null!;
}
