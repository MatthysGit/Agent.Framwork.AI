using Ai.AgentFramwork.Massar.Web.DBModels;

namespace Ai.AgentFramwork.Massar.Web.Services.Admin;

public interface IAgentService
{
    Task<List<Agent>> GetAllAsync(CancellationToken ct = default);
    Task<Agent?> GetByNameAsync(string agentName, CancellationToken ct = default);
    Task CreateAsync(Agent agent, CancellationToken ct = default);
    Task UpdateAsync(Agent agent, CancellationToken ct = default);
}
