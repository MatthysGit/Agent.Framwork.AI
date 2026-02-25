using Microsoft.Agents.AI;

namespace Ai.AgentFramwork.Massar.Web.Agents;

/// <summary>
/// Simple in-memory registry for named <see cref="AIAgent"/> instances.
/// </summary>
public interface IAgentRegistry
{
    /// <summary>Registers (or replaces) an agent by name.</summary>
    void Register(string name, AIAgent agent);

    /// <summary>Try get an agent by name.</summary>
    bool TryGet(string name, out AIAgent agent);

    /// <summary>Get an agent by name, throws if missing.</summary>
    AIAgent GetRequired(string name);

    /// <summary>Names of all registered agents.</summary>
    IReadOnlyCollection<string> Names { get; }
}
