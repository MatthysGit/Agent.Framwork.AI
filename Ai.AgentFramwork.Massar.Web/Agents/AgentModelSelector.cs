using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Ai.AgentFramwork.Massar.Web.Agents;

public sealed class AgentModelSelector : IAgentModelSelector
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AgentModelSelector>? _logger;

    private readonly ConcurrentDictionary<string, string> _models =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private volatile bool _loaded;

    private const string DefaultModel = "gpt-4.1-mini";

    public AgentModelSelector(IDbContextFactory<AppDbContext> dbFactory, ILogger<AgentModelSelector>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public string GetModelForAgent(string agentName)
    {
        EnsureLoadedSync();
        return _models.TryGetValue(agentName, out var model) && !string.IsNullOrWhiteSpace(model)
            ? model
            : DefaultModel;
    }

    public void SetModelForAgent(string agentName, string modelKey)
    {
        EnsureLoadedSync();
        SetModelForAgentSync(agentName, modelKey);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _loadGate.WaitAsync(ct);
        try
        {
            _models.Clear();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var rows = await db.Agents
                .AsNoTracking()
                .Where(a => a.IsActive)
                .Select(a => new { a.AgentName, a.AgentModel })
                .ToListAsync(ct);

            foreach (var r in rows)
            {
                if (!string.IsNullOrWhiteSpace(r.AgentName) && !string.IsNullOrWhiteSpace(r.AgentModel))
                    _models[r.AgentName] = r.AgentModel;
            }

            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private void EnsureLoadedSync()
    {
        if (_loaded) return;

        _loadGate.Wait();
        try
        {
            if (_loaded) return;

            using var db = _dbFactory.CreateDbContext();

            var rows = db.Agents
                .AsNoTracking()
                .Where(a => a.IsActive)
                .Select(a => new { a.AgentName, a.AgentModel })
                .ToList();

            foreach (var r in rows)
            {
                if (!string.IsNullOrWhiteSpace(r.AgentName) && !string.IsNullOrWhiteSpace(r.AgentModel))
                    _models[r.AgentName] = r.AgentModel;
            }

            _loaded = true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load agent models from DB. Falling back to default model.");
            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private void SetModelForAgentSync(string agentName, string modelKey)
    {
        _models[agentName] = modelKey;

        using var db = _dbFactory.CreateDbContext();

        var existing = db.Agents.FirstOrDefault(a => a.AgentName == agentName);
        if (existing is null)
        {
            db.Agents.Add(new Agent
            {
                AgentName = agentName,
                AgentModel = modelKey,
                IsActive = true,
                CreatedOn = DateTime.UtcNow
            });
        }
        else
        {
            existing.AgentModel = modelKey;
        }

        db.SaveChanges();
    }
}