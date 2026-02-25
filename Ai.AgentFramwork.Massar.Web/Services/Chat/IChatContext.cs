namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

public interface IChatContext
{
    Guid? ConversationId { get; set; }
}

public sealed class ChatContext : IChatContext
{
    public Guid? ConversationId { get; set; }
}