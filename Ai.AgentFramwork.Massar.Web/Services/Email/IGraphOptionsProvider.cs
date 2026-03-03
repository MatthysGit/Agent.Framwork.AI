using Ai.AgentFramwork.Massar.Web.DBModels;

namespace Ai.AgentFramwork.Massar.Web.Services.Email;

public interface IGraphOptionsProvider
{
    Task<GraphOptions> GetAsync(CancellationToken ct = default);
}