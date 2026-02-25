using OpenAI;
using OpenAI.Embeddings;

namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public class OpenAiEmbeddingService : IEmbeddingService
{
    // IMPORTANT:
    // Use a model that works for your app-side cosine search.
    // You can keep "text-embedding-3-large" even if SQL VECTOR can't store it.
    private const string EmbeddingModel = "text-embedding-3-large";

    private readonly OpenAIClient _client;

    public OpenAiEmbeddingService(OpenAIClient client)
    {
        _client = client;
    }

    public async Task<(string Model, float[] Vector)> GenerateAsync(string text)
    {
        // Official openai-dotnet pattern
        EmbeddingClient embeddingClient = _client.GetEmbeddingClient(EmbeddingModel);

        OpenAIEmbedding embedding = await embeddingClient.GenerateEmbeddingAsync(text);

        float[] vector = embedding.ToFloats().ToArray();

        return (EmbeddingModel, vector);
    }
}