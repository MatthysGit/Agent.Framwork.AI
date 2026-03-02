using Microsoft.Agents.AI;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Agents;

public delegate AIAgent AgentBuilder(IServiceProvider services, ChatClient chatClient);

public interface IAgentRegistry
{
    IReadOnlyCollection<string> Names { get; }

    void Register(string name, AgentBuilder builder);

    ValueTask<AIAgent> GetAsync(string name, string modelKey, CancellationToken ct = default);
}