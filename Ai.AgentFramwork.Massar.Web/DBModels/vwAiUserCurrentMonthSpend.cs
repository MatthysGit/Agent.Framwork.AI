using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class vwAiUserCurrentMonthSpend
{
    public string UserId { get; set; } = null!;

    public decimal? MonthlyCostLimitUsd { get; set; }

    public decimal? CurrentMonthSpendUsd { get; set; }

    public decimal? RemainingUsd { get; set; }
}
