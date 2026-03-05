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
        // Agent name must always be valid
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        // ⚠️ Do NOT throw on empty input:
        // Some routes (e.g., attachment-driven Excel analytics) may rely on context/attachments
        // and your orchestrator/router may pass an empty string.
        // We'll normalize to a safe default instead.
        if (string.IsNullOrWhiteSpace(input))
            input = "Use the available conversation context and any uploaded attachments to complete the requested task.";

        input = input.Trim();

        _onRoute?.Invoke(agentName);

        var modelKey = _modelSelector.GetModelForAgent(agentName);
        var agent = await _registry.GetAsync(agentName, modelKey, cancellationToken);

        // Include safe history + this user message
        var messages = new List<ChatMessage>(_historyProvider())
        {
            new(ChatRole.User, new[] { new TextContent(input) })
        };

        // ✅ Correct Agent Framework API: RunAsync returns AgentResponse
        var response = await agent.RunAsync(
            messages,
            session: null,
            options: null,
            cancellationToken: cancellationToken);

        return new AgentCallResult(agentName, response?.Text ?? string.Empty);
    }
}
