namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.EntityFrameworkCore;

public interface IDocumentToolService
{
    Task<DocumentSearchToolResult> ToolDocumentSearchAsync(
        string query,
        List<int> roleIds,
        int topK = 5,
        bool includeFilesIfAllowed = true);
}

public sealed class DocumentToolService : IDocumentToolService
{
    private readonly AppDbContext _db;
    private readonly IDocumentSearchService _search;

    public DocumentToolService(AppDbContext db, IDocumentSearchService search)
    {
        _db = db;
        _search = search;
    }

    public async Task<DocumentSearchToolResult> ToolDocumentSearchAsync(
        string query,
        List<int> roleIds,
        int topK = 5,
        bool includeFilesIfAllowed = true)
    {
        // 1) Secure semantic search (already filtered by CanRead inside your search service)
        var hits = await _search.SearchAsync(query, roleIds, topK);

        var docIds = hits.Select(h => h.DocumentId).Distinct().ToList();

        // 2) Load doc names (SourceName) for those hits
        var docMap = await _db.Documents
            .Where(d => docIds.Contains(d.DocumentId))
            .Select(d => new { d.DocumentId, d.SourceName })
            .ToDictionaryAsync(x => x.DocumentId, x => x.SourceName);

        // 3) Build tool hits
        var toolHits = hits.Select(h => new DocumentSearchToolHit
        {
            DocumentId = h.DocumentId,
            SourceName = docMap.TryGetValue(h.DocumentId, out var name) ? name : "(unknown)",
            ChunkText = h.ChunkText,
            Score = h.Score
        }).ToList();

        // 4) Optionally attach file payload for docs user can download
        var files = new List<DocumentSearchToolFile>();

        if (includeFilesIfAllowed && docIds.Count > 0)
        {
            // user must have CanDownload on doc via ANY of their roles
            var downloadableDocIds = await _db.DocumentRoleAccesses
                .Where(a =>
                    a.IsActive &&
                    a.CanDownload &&
                    roleIds.Contains(a.RoleId) &&
                    docIds.Contains(a.DocumentId))
                .Select(a => a.DocumentId)
                .Distinct()
                .ToListAsync();

            if (downloadableDocIds.Count > 0)
            {
                // pick the newest file per doc (or just first) — adjust if you store multiple
                var docFiles = await _db.DocumentFiles
                    .Where(f => downloadableDocIds.Contains(f.DocumentId))
                    .OrderByDescending(f => f.CreatedUtc)
                    .ToListAsync();

                // group: latest per doc
                var latestFiles = docFiles
                    .GroupBy(f => f.DocumentId)
                    .Select(g => g.First())
                    .ToList();

                files = latestFiles.Select(f => new DocumentSearchToolFile
                {
                    DocumentId = f.DocumentId,
                    FileName = f.FileName,
                    ContentType = f.ContentType,
                    Base64Content = Convert.ToBase64String(f.FileContent)
                }).ToList();
            }
        }

        return new DocumentSearchToolResult
        {
            Query = query,
            TopK = topK,
            Hits = toolHits,
            Files = files
        };
    }
}