using Microsoft.Agents.AI;
using System.Collections.Concurrent;

namespace Ai.AgentFramwork.Massar.Web.Agents;

public sealed class AgentRegistry : IAgentRegistry
{
    private readonly ConcurrentDictionary<string, AIAgent> _agents = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Names => _agents.Keys.ToArray();

    public void Register(string name, AIAgent agent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(agent);
        _agents[name] = agent;
    }

    public bool TryGet(string name, out AIAgent agent)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            agent = default!;
            return false;
        }

        return _agents.TryGetValue(name, out agent!);
    }

    public AIAgent GetRequired(string name)
    {
        if (TryGet(name, out var agent)) return agent;
        throw new KeyNotFoundException($"Agent '{name}' is not registered. Registered agents: {string.Join(", ", Names)}");
    }
}