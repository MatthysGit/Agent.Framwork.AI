using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Agents;

public interface IChatClientFactory
{
    ChatClient Create(string modelKey);
}