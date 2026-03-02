using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class DecisionRecord
{
    public int DecisionRecordId { get; set; }

    public Guid? ConversationId { get; set; }

    public string? OwnerUserId { get; set; }

    public string Decision { get; set; } = null!;

    public string? Rationale { get; set; }

    public string? RelatedDocumentsJson { get; set; }

    public decimal ConfidenceScore { get; set; }

    public DateTime CreatedOn { get; set; }
}
