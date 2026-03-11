using Ai.AgentFramwork.Massar.Web.Services.Chat.DecisionTracking;
using System.Collections.Concurrent;

namespace Ai.AgentFramwork.Massar.Web.Services;

public sealed class RouterDecisionTrackerService : IRouterDecisionTrackerService
{
    private readonly ConcurrentQueue<string> _steps = new();

    public IReadOnlyList<string> Steps => _steps.ToList();

    public Task TrackAsync(string stage, string detail, CancellationToken ct = default)
    {
        _steps.Enqueue($"{stage}: {detail}");
        return Task.CompletedTask;
    }
}

public interface IRouterDecisionTrackerService
{
    Task TrackAsync(
        string stage,
        string detail,
        CancellationToken ct = default);
}

