using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ai.AgentFramwork.Massar.Web.Services.Chat;

namespace Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;

public interface IUnifiedDocumentSearchService
{
    Task<IReadOnlyList<DocumentSearchHit>> SearchAsync(
        string query,
        Guid conversationId,
        IReadOnlyCollection<int> roleIds,
        string embeddingModel,
        int topK = 8,
        CancellationToken ct = default);


    Task<IReadOnlyList<DocumentSearchHit>> SearchScopedAsync(
        string query,
        Guid conversationId,
        string embeddingModel,
        int topK = 8,
        CancellationToken ct = default);


}

public sealed class UnifiedDocumentSearchService : IUnifiedDocumentSearchService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEmbeddingProvider _embeddings;
    
    
    public UnifiedDocumentSearchService(
        IDbContextFactory<AppDbContext> dbFactory,
        IEmbeddingProvider embeddings
        )
    {
        _dbFactory = dbFactory;
        _embeddings = embeddings;
           
    }


    public async Task<IReadOnlyList<DocumentSearchHit>> SearchScopedAsync(
      string query,
      Guid conversationId,
      string embeddingModel,
      int topK = 8,
      CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<DocumentSearchHit>();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 1) Embed query
        var q = await _embeddings.EmbedAsync(query, embeddingModel, ct);

        

        // 2) Scoped candidates: only this conversation
        var scopedCandidates = await (
            from ing in db.ChatConversationAttachmentIngests.AsNoTracking()
            join c in db.ChatConversationAttachmentChunks.AsNoTracking()
                on ing.ChatConversationAttachmentIngestId equals c.ChatConversationAttachmentIngestId
            join e in db.ChatConversationAttachmentChunkEmbeddings.AsNoTracking()
                on c.ChatConversationAttachmentChunkId equals e.ChatConversationAttachmentChunkId
            where ing.ConversationId == conversationId
               && e.EmbeddingModel == embeddingModel
            select new
            {
                ing.AttachmentId,
                ing.FileName,
                c.Text,
                e.VectorBinary
            }
        ).ToListAsync(ct);

        
        var scopedHits = scopedCandidates
            .Select(x =>
            {
                var v = VectorBytes.ToFloatArray(x.VectorBinary);
                var score = Similarity.Cosine(q, v);
                return new DocumentSearchHit(
                    Store: DocStore.Scoped,
                    Score: score,
                    Title: x.FileName,
                    Snippet: Snip(x.Text),
                    PageNumber: null,
                    DocumentId: null,
                    DocumentFileId: null,
                    AttachmentId: x.AttachmentId
                );
            })
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();

        

        // 6) Merge overall topK
        return scopedHits
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }

    public async Task<IReadOnlyList<DocumentSearchHit>> SearchAsync(
        string query,
        Guid conversationId,
        IReadOnlyCollection<int> roleIds,
        string embeddingModel,
        int topK = 8,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<DocumentSearchHit>();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 1) Embed query
        var q = await _embeddings.EmbedAsync(query, embeddingModel, ct);

        // 2) Global candidates: only docs user can read
        var globalCandidates = await (
            from dc in db.DocumentChunks.AsNoTracking()
            join e in db.ChunkEmbeddings.AsNoTracking()
                on dc.ChunkId equals e.ChunkId
            join d in db.Documents.AsNoTracking()
                on dc.DocumentId equals d.DocumentId
            join a in db.DocumentRoleAccesses.AsNoTracking()
                on d.DocumentId equals a.DocumentId
            where a.IsActive
               && a.CanRead
               && roleIds.Contains(a.RoleId)
               && e.EmbeddingModel == embeddingModel
            select new
            {
                d.DocumentId,
                d.OriginalFileName,
                dc.PageNumber,
                dc.Text,
                e.VectorBinary
            }
        ).ToListAsync(ct);

        // 3) Scoped candidates: only this conversation
        var scopedCandidates = await (
            from ing in db.ChatConversationAttachmentIngests.AsNoTracking()
            join c in db.ChatConversationAttachmentChunks.AsNoTracking()
                on ing.ChatConversationAttachmentIngestId equals c.ChatConversationAttachmentIngestId
            join e in db.ChatConversationAttachmentChunkEmbeddings.AsNoTracking()
                on c.ChatConversationAttachmentChunkId equals e.ChatConversationAttachmentChunkId
            where ing.ConversationId == conversationId
               && e.EmbeddingModel == embeddingModel
            select new
            {
                ing.AttachmentId,
                ing.FileName,
                c.Text,
                e.VectorBinary
            }
        ).ToListAsync(ct);

        // 4) Score
        var globalHits = globalCandidates
            .Select(x =>
            {
                var v = VectorBytes.ToFloatArray(x.VectorBinary);
                var score = Similarity.Cosine(q, v);
                return new DocumentSearchHit(
                    Store: DocStore.Global,
                    Score: score,
                    Title: x.OriginalFileName ?? "(global document)",
                    Snippet: Snip(x.Text),
                    PageNumber: x.PageNumber,
                    DocumentId: x.DocumentId,
                    DocumentFileId: null,      // resolve below
                    AttachmentId: null
                );
            })
            .OrderByDescending(h => h.Score)
            .Take(topK * 3) // a bit extra before resolving files
            .ToList();

        var scopedHits = scopedCandidates
            .Select(x =>
            {
                var v = VectorBytes.ToFloatArray(x.VectorBinary);
                var score = Similarity.Cosine(q, v);
                return new DocumentSearchHit(
                    Store: DocStore.Scoped,
                    Score: score,
                    Title: x.FileName,
                    Snippet: Snip(x.Text),
                    PageNumber: null,
                    DocumentId: null,
                    DocumentFileId: null,
                    AttachmentId: x.AttachmentId
                );
            })
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();

        // 5) Resolve newest DocumentFileId for each global DocumentId
        var docIds = globalHits.Select(h => h.DocumentId!.Value).Distinct().ToList();

        var fileMap = await db.DocumentFiles.AsNoTracking()
            .Where(f => docIds.Contains(f.DocumentId))
            .GroupBy(f => f.DocumentId)
            .Select(g => g.OrderByDescending(x => x.CreatedUtc).First())
            .ToDictionaryAsync(x => x.DocumentId, x => x.DocumentFileId, ct);

        var finalizedGlobal = globalHits
            .Select(h => h with { DocumentFileId = fileMap.GetValueOrDefault(h.DocumentId!.Value) })
            .Where(h => h.DocumentFileId != null)
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();

        // 6) Merge overall topK
        return finalizedGlobal
            .Concat(scopedHits)
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }

    private static string Snip(string s, int max = 280)
        => string.IsNullOrWhiteSpace(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}

internal static class Similarity
{
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;

        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var x = a[i];
            var y = b[i];
            dot += x * y;
            na += x * x;
            nb += y * y;
        }

        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        if (denom <= 0) return 0;
        return dot / denom;
    }
}

internal static class VectorBytes
{
    // Assumes float32 little-endian serialization: byte[] length == 4 * dims
    public static float[] ToFloatArray(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0) return Array.Empty<float>();
        if (bytes.Length % 4 != 0) return Array.Empty<float>();

        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}