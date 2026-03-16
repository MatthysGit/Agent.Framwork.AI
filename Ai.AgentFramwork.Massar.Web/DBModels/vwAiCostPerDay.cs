using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class vwAiCostPerDay
{
    public DateOnly? DayUtc { get; set; }

    public decimal? TotalCostUsd { get; set; }

    public int? TotalTokens { get; set; }

    public long? CallCount { get; set; }
}
