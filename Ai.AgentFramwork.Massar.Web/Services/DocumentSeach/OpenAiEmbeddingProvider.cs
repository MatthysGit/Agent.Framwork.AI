using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Embeddings;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;

#pragma warning disable OPENAI001
public sealed class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    private readonly IConfiguration _cfg;

    public OpenAiEmbeddingProvider(IConfiguration cfg)
        => _cfg = cfg;

    public async Task<float[]> EmbedAsync(string text, string model, CancellationToken ct = default)
    {
        var key = _cfg["OpenAI:Key"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OpenAI:Key missing.");

        var client = new OpenAIClient(key);
        var embClient = client.GetEmbeddingClient(model);

        // The SDK returns OpenAIEmbedding. Extract floats via ToFloats().
        OpenAIEmbedding embedding = await embClient.GenerateEmbeddingAsync(text, cancellationToken: ct);
        return embedding.ToFloats().ToArray();
    }
}