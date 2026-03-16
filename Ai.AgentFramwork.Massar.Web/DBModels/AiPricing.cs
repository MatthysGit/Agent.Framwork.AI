using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class AiPricing
{
    public string ModelName { get; set; } = null!;

    public decimal InputCostPer1M { get; set; }

    public decimal? CachedInputCostPer1M { get; set; }

    public decimal OutputCostPer1M { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedOn { get; set; }

    public virtual ICollection<AiTokenUsageLog> AiTokenUsageLogs { get; set; } = new List<AiTokenUsageLog>();
}
