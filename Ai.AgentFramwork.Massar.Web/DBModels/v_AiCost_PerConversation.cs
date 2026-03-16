using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class v_AiCost_PerConversation
{
    public Guid ConversationId { get; set; }

    public string UserId { get; set; } = null!;

    public int? TotalTokens { get; set; }

    public decimal? TotalCostUsd { get; set; }

    public DateTime? FirstCallOn { get; set; }

    public DateTime? LastCallOn { get; set; }
}
