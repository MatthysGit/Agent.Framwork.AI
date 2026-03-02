using Microsoft.Agents.AI;
using OpenAI.Chat;
using System.Collections.Concurrent;

namespace Ai.AgentFramwork.Massar.Web.Agents;

public sealed class AgentRegistry : IAgentRegistry
{
    private readonly IServiceProvider _services;
    private readonly IChatClientFactory _chatClientFactory;

    private readonly ConcurrentDictionary<string, AgentBuilder> _builders =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<(string Name, string ModelKey), Lazy<AIAgent>> _cache =
        new();

    public AgentRegistry(IServiceProvider services, IChatClientFactory chatClientFactory)
    {
        _services = services;
        _chatClientFactory = chatClientFactory;
    }

    public IReadOnlyCollection<string> Names => _builders.Keys.ToArray();

    public void Register(string name, AgentBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(builder);

        _builders[name] = builder;

        // Clear cached agents for this name (optional, but avoids stale builds if you re-register).
        foreach (var key in _cache.Keys)
        {
            if (string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase))
                _cache.TryRemove(key, out _);
        }
    }

    public ValueTask<AIAgent> GetAsync(string name, string modelKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKey);

        if (!_builders.TryGetValue(name, out var builder))
            throw new KeyNotFoundException(
                $"Agent '{name}' is not registered. Registered agents: {string.Join(", ", Names)}");

        var lazy = _cache.GetOrAdd(
            (name, modelKey),
            _ => new Lazy<AIAgent>(() =>
            {
                var chatClient = _chatClientFactory.Create(modelKey);
                return builder(_services, chatClient);
            }, LazyThreadSafetyMode.ExecutionAndPublication));

        return ValueTask.FromResult(lazy.Value);
    }
}