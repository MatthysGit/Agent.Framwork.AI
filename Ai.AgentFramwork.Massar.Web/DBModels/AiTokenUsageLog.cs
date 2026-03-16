using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class AiTokenUsageLog
{
    public Guid AiTokenUsageLogId { get; set; }

    public Guid? ConversationId { get; set; }

    public string UserId { get; set; } = null!;

    public Guid? MessageId { get; set; }

    public string AgentName { get; set; } = null!;

    public string ModelName { get; set; } = null!;

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public int? TotalTokens { get; set; }

    public int? CachedInputTokens { get; set; }

    public int? ReasoningTokens { get; set; }

    public decimal? InputCostUsd { get; set; }

    public decimal? OutputCostUsd { get; set; }

    public decimal? TotalCostUsd { get; set; }

    public string? RequestId { get; set; }

    public string? ClientRequestId { get; set; }

    public int? LatencyMs { get; set; }

    public bool Succeeded { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedOn { get; set; }

    public virtual Agent AgentNameNavigation { get; set; } = null!;

    public virtual ChatConversation Conversation { get; set; } = null!;

    public virtual AiPricing ModelNameNavigation { get; set; } = null!;

    public virtual AppUser User { get; set; } = null!;
}
