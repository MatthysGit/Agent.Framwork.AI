using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class vwAiCostPerConversation
{
    public Guid ConversationId { get; set; }

    public string UserId { get; set; } = null!;

    public decimal? TotalCostUsd { get; set; }

    public int? TotalTokens { get; set; }

    public long? CallCount { get; set; }

    public DateTime? FirstCallUtc { get; set; }

    public DateTime? LastCallUtc { get; set; }
}
