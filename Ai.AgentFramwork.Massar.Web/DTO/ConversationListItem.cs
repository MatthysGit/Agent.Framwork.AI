namespace Ai.AgentFramwork.Massar.Web.DTO;

public sealed class ConversationListItem
{
    public Guid ConversationId { get; set; }
    public string? Title { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public bool IsArchived { get; set; }
    public int MessageCount { get; set; }
}
