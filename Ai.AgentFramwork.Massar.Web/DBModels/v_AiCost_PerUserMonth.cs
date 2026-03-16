using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class v_AiCost_PerUserMonth
{
    public string UserId { get; set; } = null!;

    public DateOnly? MonthStart { get; set; }

    public decimal? TotalCostUsd { get; set; }
}
