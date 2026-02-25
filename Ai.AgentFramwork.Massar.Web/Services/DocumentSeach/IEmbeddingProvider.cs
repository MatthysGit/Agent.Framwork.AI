using System.Threading;
using System.Threading.Tasks;

namespace Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;

public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string text, string model, CancellationToken ct = default);
}