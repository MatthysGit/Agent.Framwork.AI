using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.EntityFrameworkCore;

namespace Ai.AgentFramwork.Massar.Web.Services.Admin;

public sealed class AgentService : IAgentService
{
    private readonly AppDbContext _db;

    public AgentService(AppDbContext db)
    {
        _db = db;
    }

    public Task<List<Agent>> GetAllAsync(CancellationToken ct = default)
        => _db.Agents
            .AsNoTracking()
            .OrderBy(a => a.AgentName)
            .ToListAsync(ct);

    public Task<Agent?> GetByNameAsync(string agentName, CancellationToken ct = default)
        => _db.Agents.FirstOrDefaultAsync(a => a.AgentName == agentName, ct);

    public async Task CreateAsync(Agent agent, CancellationToken ct = default)
    {
        // basic server-side guard
        var exists = await _db.Agents.AnyAsync(a => a.AgentName == agent.AgentName, ct);
        if (exists)
            throw new InvalidOperationException($"Agent '{agent.AgentName}' already exists.");

        agent.CreatedOn = DateTime.UtcNow;

        _db.Agents.Add(agent);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Agent agent, CancellationToken ct = default)
    {
        var dbAgent = await _db.Agents.FirstOrDefaultAsync(a => a.AgentName == agent.AgentName, ct);
        if (dbAgent is null)
            throw new InvalidOperationException($"Agent '{agent.AgentName}' was not found.");

        dbAgent.AgentModel = agent.AgentModel;
        dbAgent.IsActive = agent.IsActive;

        await _db.SaveChangesAsync(ct);
    }
}
