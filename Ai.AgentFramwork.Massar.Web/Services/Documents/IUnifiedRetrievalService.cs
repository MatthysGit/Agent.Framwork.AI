namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public interface IUnifiedRetrievalService
{
    Task<IReadOnlyList<RetrievalHit>> SearchAsync(
        string query,
        Guid? conversationId,
        int topK = 8,
        string? embeddingModel = null,
        CancellationToken ct = default);
}