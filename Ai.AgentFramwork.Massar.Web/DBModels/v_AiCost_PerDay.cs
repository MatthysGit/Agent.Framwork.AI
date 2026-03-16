using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class v_AiCost_PerDay
{
    public DateOnly? Day { get; set; }

    public int? TotalTokens { get; set; }

    public decimal? TotalCostUsd { get; set; }
}
