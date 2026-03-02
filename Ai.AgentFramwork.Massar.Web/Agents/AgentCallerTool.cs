// File: Agents/AgentCallerTool.cs
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.ComponentModel;

namespace Ai.AgentFramwork.Massar.Web.Agents;

public sealed class AgentCallerTool
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentModelSelector _modelSelector;
    private readonly Func<IReadOnlyList<ChatMessage>> _historyProvider;
    private readonly Action<string>? _onRoute;

    public AgentCallerTool(
        IAgentRegistry registry,
        IAgentModelSelector modelSelector,
        Func<IReadOnlyList<ChatMessage>> historyProvider,
        Action<string>? onRoute = null)
    {
        _registry = registry;
        _modelSelector = modelSelector;
        _historyProvider = historyProvider;
        _onRoute = onRoute;
    }

    public sealed record AgentCallResult(string AgentName, string Text);

    // ✅ Single overload
    // ✅ Has parameter named `cancellationToken` to match your call sites
    [Description("Calls a specialized agent and returns its final response.")]
    public async Task<AgentCallResult> CallAgentAsync(
        [Description("The specialized agent name to call.")] string agentName,
        [Description("The user request for that agent.")] string input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        _onRoute?.Invoke(agentName);

        var modelKey = _modelSelector.GetModelForAgent(agentName);
        var agent = await _registry.GetAsync(agentName, modelKey, cancellationToken);

        // Include safe history + this user message
        var messages = new List<ChatMessage>(_historyProvider())
        {
            new(ChatRole.User, new[] { new TextContent(input) })
        };

        // ✅ Correct Agent Framework API: RunAsync returns AgentResponse :contentReference[oaicite:1]{index=1}
        var response = await agent.RunAsync(
            messages,
            session: null,
            options: null,
            cancellationToken: cancellationToken);

        return new AgentCallResult(agentName, response?.Text ?? string.Empty);
    }
}