namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

public class DocumentSearchService : IDocumentSearchService
{
    private readonly AppDbContext _db;
    private readonly IEmbeddingService _embedding;
    private readonly IMemoryCache _cache;

    public DocumentSearchService(AppDbContext db, IEmbeddingService embedding, IMemoryCache cache)
    {
        _db = db;
        _embedding = embedding;
        _cache = cache;
    }

    // ✅ Backward-compatible: existing callers keep working
    public Task<List<SearchHit>> SearchAsync(string query, List<int> roleIds, int topK = 5)
        => SearchAsync(query, roleIds, conversationId: null, topK: topK);

    // ✅ New: one call searches BOTH authorized docs + chat-temp docs (scoped by conversation)
    public async Task<List<SearchHit>> SearchAsync(string query, List<int> roleIds, Guid? conversationId, int topK = 5)
    {

      
        
        if (string.IsNullOrWhiteSpace(query))
            return new List<SearchHit>();

        var (model, qVec) = await GetQueryEmbeddingAsync(query);

        Console.WriteLine($">>> DocumentSearchService.SearchAsync: conversationId={conversationId?.ToString() ?? "(null)"} model={model}");
        

        // -------------------------
        // 1) Authorized docs (existing)
        // -------------------------
        var allowedDocs = await _db.DocumentRoleAccesses
            .AsNoTracking()
            .Where(a => a.IsActive && a.CanRead && roleIds.Contains(a.RoleId))
            .Select(a => a.DocumentId)
            .Distinct()
            .ToListAsync();

        var docData = await _db.ChunkEmbeddings
            .AsNoTracking()
            .Where(e => e.EmbeddingModel == model)
            .Select(e => new
            {
                e.ChunkId,
                e.VectorBinary,
                DocumentId = e.Chunk.DocumentId,
                Text = e.Chunk.Text
            })
            .ToListAsync();

        var docHits = docData
            .Where(x => allowedDocs.Contains(x.DocumentId))
            .Select(x =>
            {
                var vec = EmbeddingBinary.ToFloats(x.VectorBinary);
                return new SearchHit(x.DocumentId, x.Text, Cosine(qVec, vec));
            })
            .ToList();

        // -------------------------
        // 2) Chat-temp docs (NEW)
        //    scoped to conversationId
        // -------------------------
        var chatHits = new List<SearchHit>();

        if (conversationId.HasValue)
        {
            // NOTE: Replace DbSet names below if your context uses different pluralization.
            // Expected:
            //   _db.ChatConversationAttachmentIngests
            //   _db.ChatConversationAttachmentChunks
            //   _db.ChatConversationAttachmentChunkEmbeddings

            var chatData = await (
                from ingest in _db.ChatConversationAttachmentIngests.AsNoTracking()
                join chunk in _db.ChatConversationAttachmentChunks.AsNoTracking()
                    on ingest.ChatConversationAttachmentIngestId equals chunk.ChatConversationAttachmentIngestId
                join emb in _db.ChatConversationAttachmentChunkEmbeddings.AsNoTracking()
                    on chunk.ChatConversationAttachmentChunkId equals emb.ChatConversationAttachmentChunkId
                where ingest.ConversationId == conversationId.Value
                      && emb.EmbeddingModel == model
                select new
                {
                    emb.VectorBinary,
                    chunk.Text
                })
                .ToListAsync();

            chatHits = chatData
                .Select(x =>
                {
                    var vec = EmbeddingBinary.ToFloats(x.VectorBinary);
                    return new SearchHit(Guid.Empty, x.Text, Cosine(qVec, vec)); // Guid.Empty = chat-only
                })
                .ToList();
        }

        // -------------------------
        // 3) Merge + TopK
        // -------------------------
        var hits = docHits
            .Concat(chatHits)
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();

        return hits;
    }

    private async Task<(string Model, float[] Vector)> GetQueryEmbeddingAsync(string query)
    {
        return await _cache.GetOrCreateAsync($"qemb:{query}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await _embedding.GenerateAsync(query);
        });
    }

    private static double Cosine(float[] v1, float[] v2)
    {
        if (v1.Length == 0 || v2.Length == 0)
            return 0;

        var len = Math.Min(v1.Length, v2.Length);

        double dot = 0, mag1 = 0, mag2 = 0;
        for (int i = 0; i < len; i++)
        {
            dot += v1[i] * v2[i];
            mag1 += v1[i] * v1[i];
            mag2 += v2[i] * v2[i];
        }

        if (mag1 == 0 || mag2 == 0)
            return 0;

        return dot / (Math.Sqrt(mag1) * Math.Sqrt(mag2));
    }
}