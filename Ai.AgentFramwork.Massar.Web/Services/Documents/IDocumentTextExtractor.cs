namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public interface IDocumentTextExtractor
{
    Task<string> ExtractAsync(byte[] content, string contentType, string fileName);
}