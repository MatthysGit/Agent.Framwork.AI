using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class v_AiCost_PerUser
{
    public string UserId { get; set; } = null!;

    public int? TotalTokens { get; set; }

    public decimal? TotalCostUsd { get; set; }
}
