using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.EntityFrameworkCore;

namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public class ChunkReindexService : IChunkReindexService
{
    private readonly AppDbContext _db;
    private readonly IEmbeddingService _embedding;

    public ChunkReindexService(AppDbContext db, IEmbeddingService embedding)
    {
        _db = db;
        _embedding = embedding;
    }

    public async Task ReindexMissingAsync(string model)
    {
        var chunks = await _db.DocumentChunks
            .Include(c => c.ChunkEmbeddings)
            .ToListAsync();

        foreach (var c in chunks)
        {
            // Skip if this chunk already has an embedding for this model
            if (c.ChunkEmbeddings.Any(e => e.EmbeddingModel == model))
                continue;

            // Generate embedding
            var (_, vec) = await _embedding.GenerateAsync(c.Text);

            _db.ChunkEmbeddings.Add(new ChunkEmbedding
            {
                ChunkId = c.ChunkId,                  // <-- use existing chunk id
                EmbeddingModel = model,
                Dimensions = vec.Length,              // <-- vec, not vector
                VectorBinary = EmbeddingBinary.ToBytes(vec), // <-- vec, not vector
                CreatedUtc = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
    }
}