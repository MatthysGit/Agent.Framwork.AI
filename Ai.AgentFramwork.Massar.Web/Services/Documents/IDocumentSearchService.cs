namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public interface IDocumentSearchService
{
    Task<List<SearchHit>> SearchAsync(string query, List<int> roleIds, int topK = 5);
    Task<List<SearchHit>> SearchAsync(string query, List<int> roleIds, Guid? conversationId, int topK = 5);
    
}

public record SearchHit(Guid DocumentId, string ChunkText, double Score);