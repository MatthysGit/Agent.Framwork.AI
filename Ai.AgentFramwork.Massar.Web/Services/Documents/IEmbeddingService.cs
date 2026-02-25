namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public interface IEmbeddingService
{
    Task<(string Model, float[] Vector)> GenerateAsync(string text);
}