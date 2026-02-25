namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.EntityFrameworkCore;

public sealed class UnifiedRetrievalService : IUnifiedRetrievalService
{
    private readonly AppDbContext _db;
    private readonly IEmbeddingService _embedding;

    public UnifiedRetrievalService(AppDbContext db, IEmbeddingService embedding)
    {
        _db = db;
        _embedding = embedding;
    }

    public async Task<IReadOnlyList<RetrievalHit>> SearchAsync(
        string query,
        Guid? conversationId,
        int topK = 8,
        string? embeddingModel = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<RetrievalHit>();

        // 1) Embed the query
        var (model, qVec) = await _embedding.GenerateAsync(query);
        var targetModel = embeddingModel ?? model;

        // 2) Pull candidates from BOTH stores (keep these caps to avoid loading huge tables)
        // Tune these numbers as needed.
        const int authorizedCandidateLimit = 1500;
        const int chatCandidateLimit = 800;

        var authorizedCandidates = await LoadAuthorizedCandidatesAsync(targetModel, authorizedCandidateLimit, ct);
        var chatCandidates = conversationId.HasValue
            ? await LoadChatCandidatesAsync(conversationId.Value, targetModel, chatCandidateLimit, ct)
            : new List<(string Text, byte[] VectorBinary, Guid DocumentId, Guid? AttachmentId)>();

        // 3) Score + merge
        var hits = new List<RetrievalHit>(authorizedCandidates.Count + chatCandidates.Count);

        foreach (var c in authorizedCandidates)
        {
            var v = BytesToFloatArray(c.VectorBinary);
            var score = CosineSimilarity(qVec, v);
            hits.Add(new RetrievalHit(
                Source: "authorized",
                Text: c.Text,
                Score: score,
                DocumentId: c.DocumentId));
        }

        foreach (var c in chatCandidates)
        {
            var v = BytesToFloatArray(c.VectorBinary);
            var score = CosineSimilarity(qVec, v);
            hits.Add(new RetrievalHit(
                Source: "chat",
                Text: c.Text,
                Score: score,
                DocumentId: null,
                AttachmentId: c.AttachmentId,
                ConversationId: conversationId));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }

    private async Task<List<(string Text, byte[] VectorBinary, Guid DocumentId)>> LoadAuthorizedCandidatesAsync(
        string embeddingModel,
        int limit,
        CancellationToken ct)
    {
        // Join DocumentChunks -> ChunkEmbeddings
        // Assumes: ChunkEmbedding.ChunkId FK to DocumentChunk.ChunkId
        var rows = await (
            from c in _db.DocumentChunks.AsNoTracking()
            join e in _db.ChunkEmbeddings.AsNoTracking()
                on c.ChunkId equals e.ChunkId
            where e.EmbeddingModel == embeddingModel
            orderby c.CreatedUtc descending
            select new
            {
                c.DocumentId,
                c.Text,
                e.VectorBinary
            })
            .Take(limit)
            .ToListAsync(ct);

        return rows
            .Select(r => (r.Text, r.VectorBinary, r.DocumentId))
            .ToList();
    }

    private async Task<List<(string Text, byte[] VectorBinary, Guid DocumentId, Guid? AttachmentId)>> LoadChatCandidatesAsync(
        Guid conversationId,
        string embeddingModel,
        int limit,
        CancellationToken ct)
    {
        // Join:
        // ChatConversationAttachmentIngest (ConversationId)
        //   -> ChatConversationAttachmentChunk
        //      -> ChatConversationAttachmentChunkEmbedding
        //
        // Uses your new temp tables; does NOT touch Documents*.
        var rows = await (
            from ingest in _db.ChatConversationAttachmentIngests.AsNoTracking()
            join chunk in _db.ChatConversationAttachmentChunks.AsNoTracking()
                on ingest.ChatConversationAttachmentIngestId equals chunk.ChatConversationAttachmentIngestId
            join emb in _db.ChatConversationAttachmentChunkEmbeddings.AsNoTracking()
                on chunk.ChatConversationAttachmentChunkId equals emb.ChatConversationAttachmentChunkId
            where ingest.ConversationId == conversationId
                  && emb.EmbeddingModel == embeddingModel
            orderby chunk.CreatedUtc descending
            select new
            {
                ingest.AttachmentId,
                chunk.Text,
                emb.VectorBinary
            })
            .Take(limit)
            .ToListAsync(ct);

        // DocumentId is not applicable for chat-temp; we keep signature aligned
        return rows
            .Select(r => (r.Text, r.VectorBinary, DocumentId: Guid.Empty, AttachmentId: (Guid?)r.AttachmentId))
            .ToList();
    }

    private static float[] BytesToFloatArray(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return Array.Empty<float>();

        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;

        var len = Math.Min(a.Length, b.Length);

        double dot = 0;
        double na = 0;
        double nb = 0;

        for (int i = 0; i < len; i++)
        {
            var x = a[i];
            var y = b[i];
            dot += x * y;
            na += x * x;
            nb += y * y;
        }

        if (na == 0 || nb == 0)
            return 0;

        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}