namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public interface IChunkReindexService
{
    Task ReindexMissingAsync(string model);
}