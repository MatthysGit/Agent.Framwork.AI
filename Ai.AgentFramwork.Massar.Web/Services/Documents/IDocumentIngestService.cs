namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

public interface IDocumentIngestService
{
    Task<Guid> UploadAsync(IBrowserFile file, Guid? userId, int documentCategoryId);
}

public class DocumentIngestService : IDocumentIngestService
{
    private readonly AppDbContext _db;
    private readonly IDocumentTextExtractor _extractor;
    private readonly ITextChunker _chunker;
    private readonly IEmbeddingService _embedding;
    private readonly IPdfOcrService _pdfOcr;
    private readonly ILogger<DocumentIngestService> _logger;

    public DocumentIngestService(
        AppDbContext db,
        IDocumentTextExtractor extractor,
        ITextChunker chunker,
        IEmbeddingService embedding,
        IPdfOcrService pdfOcr,
        ILogger<DocumentIngestService> logger)
    {
        _db = db;
        _extractor = extractor;
        _chunker = chunker;
        _embedding = embedding;
        _pdfOcr = pdfOcr;
        _logger = logger;
    }

    public async Task<Guid> UploadAsync(IBrowserFile file, Guid? userId, int documentCategoryId)
    {
        if (file is null)
            throw new InvalidOperationException("File is required.");

        if (documentCategoryId <= 0)
            throw new InvalidOperationException("DocumentCategory is required.");

        var categoryOk = await _db.DocumentCategories
            .AsNoTracking()
            .AnyAsync(c => c.DocumentCategoryId == documentCategoryId && c.IsActive);

        if (!categoryOk)
            throw new InvalidOperationException("Invalid DocumentCategory selected.");

        // 1) Read file bytes
        byte[] bytes;
        using (var stream = file.OpenReadStream(maxAllowedSize: 200 * 1024 * 1024))
        using (var ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        // 2) Hash for dedupe (still useful)
        var newHash = SHA256.HashData(bytes);

        // Find existing doc by business key (OriginalFileName)
        var existingDocId = await _db.Documents
            .Where(d => d.OriginalFileName == file.Name)
            .Select(d => d.DocumentId)
            .FirstOrDefaultAsync();

        Guid docId;

        if (existingDocId != Guid.Empty)
        {
            docId = existingDocId;

            // If exact same content already stored, just update category mapping and exit
            var sameHash = await _db.Documents
                .AnyAsync(d => d.DocumentId == docId && d.ContentHash == newHash);

            if (sameHash)
            {
                await _db.DocumentCategoryMaps
                    .Where(m => m.DocumentId == docId)
                    .ExecuteDeleteAsync();

                // ExecuteDeleteAsync bypasses the change tracker; clear tracked instances
                _db.ChangeTracker.Clear();

                _db.DocumentCategoryMaps.Add(new DocumentCategoryMap
                {
                    DocumentId = docId,
                    DocumentCategoryId = documentCategoryId,
                    CreatedUtc = DateTime.UtcNow
                });

                await _db.SaveChangesAsync();
                return docId;
            }

            _logger.LogWarning("Updating existing document (replace content): DocumentId={DocumentId}, File={FileName}", docId, file.Name);

            // Update document metadata/hash
            await _db.Documents
                .Where(d => d.DocumentId == docId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(d => d.SourceName, Path.GetFileNameWithoutExtension(file.Name))
                    .SetProperty(d => d.OriginalFileName, file.Name)
                    .SetProperty(d => d.ContentHash, newHash)
                    .SetProperty(d => d.CreatedUtc, DateTime.UtcNow));

            // Delete embeddings first (FK_ChunkEmbeddings_Chunks)
            var chunkIdsQuery = _db.DocumentChunks
                .Where(c => c.DocumentId == docId)
                .Select(c => c.ChunkId);

            await _db.ChunkEmbeddings
                .Where(e => chunkIdsQuery.Contains(e.ChunkId))
                .ExecuteDeleteAsync();

            // Delete chunks
            await _db.DocumentChunks
                .Where(c => c.DocumentId == docId)
                .ExecuteDeleteAsync();

            // Replace file rows (delete then insert)
            await _db.DocumentFiles
                .Where(f => f.DocumentId == docId)
                .ExecuteDeleteAsync();

            // ExecuteDeleteAsync bypasses the change tracker; clear tracked instances
            _db.ChangeTracker.Clear();

            _db.DocumentFiles.Add(new DocumentFile
            {
                DocumentFileId = Guid.NewGuid(),
                DocumentId = docId,
                FileName = file.Name,
                ContentType = file.ContentType,
                FileExtension = Path.GetExtension(file.Name),
                FileSizeBytes = file.Size,
                FileContent = bytes,
                CreatedUtc = DateTime.UtcNow
            });

            // Replace category map (one category per document)
            await _db.DocumentCategoryMaps
                .Where(m => m.DocumentId == docId)
                .ExecuteDeleteAsync();

            // ExecuteDeleteAsync bypasses the change tracker; clear tracked instances
            _db.ChangeTracker.Clear();

            _db.DocumentCategoryMaps.Add(new DocumentCategoryMap
            {
                DocumentId = docId,
                DocumentCategoryId = documentCategoryId,
                CreatedUtc = DateTime.UtcNow
            });
        }
        else
        {
            // New document
            docId = Guid.NewGuid();

            _logger.LogInformation("Creating new document: DocumentId={DocumentId}, File={FileName}", docId, file.Name);

            _db.Documents.Add(new Document
            {
                DocumentId = docId,
                SourceName = Path.GetFileNameWithoutExtension(file.Name),
                OriginalFileName = file.Name,
                ContentHash = newHash,
                CreatedUtc = DateTime.UtcNow
            });

            _db.DocumentFiles.Add(new DocumentFile
            {
                DocumentFileId = Guid.NewGuid(),
                DocumentId = docId,
                FileName = file.Name,
                ContentType = file.ContentType,
                FileExtension = Path.GetExtension(file.Name),
                FileSizeBytes = file.Size,
                FileContent = bytes,
                CreatedUtc = DateTime.UtcNow
            });

            _db.DocumentCategoryMaps.Add(new DocumentCategoryMap
            {
                DocumentId = docId,
                DocumentCategoryId = documentCategoryId,
                CreatedUtc = DateTime.UtcNow
            });
        }

        // 3) Extract text (primary)
        var text = await _extractor.ExtractAsync(bytes, file.ContentType, file.Name);

        // OCR fallback if primary extractor returns nothing (PDF only)
        if (string.IsNullOrWhiteSpace(text) && IsPdf(file))
        {
            _logger.LogWarning("Primary extraction produced no text for {FileName}. Using OCR fallback.", file.Name);
            text = await _pdfOcr.ExtractTextAsync(bytes);
            _logger.LogInformation("OCR fallback result for {FileName}: extractedChars={Chars}", file.Name, text?.Length ?? 0);
        }

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("No text extracted (scanned PDF or unsupported format).");

        // 5) Chunk
        var chunks = _chunker.Split(text, maxChars: 4000, overlapChars: 400);

        // 6) Save chunks + embeddings
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunkText = chunks[i];
            var chunkId = Guid.NewGuid();

            _db.DocumentChunks.Add(new DocumentChunk
            {
                ChunkId = chunkId,
                DocumentId = docId,
                ChunkIndex = i,
                Text = chunkText,
                CreatedUtc = DateTime.UtcNow
            });

            var (model, vector) = await _embedding.GenerateAsync(chunkText);

            _db.ChunkEmbeddings.Add(new ChunkEmbedding
            {
                ChunkId = chunkId,
                EmbeddingModel = model,
                Dimensions = vector.Length,
                VectorBinary = FloatArrayToBytes(vector),
                CreatedUtc = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        return docId;
    }

    private static bool IsPdf(IBrowserFile file)
    {
        if (string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = Path.GetExtension(file.Name);
        return string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] FloatArrayToBytes(float[] array)
    {
        var bytes = new byte[array.Length * sizeof(float)];
        Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}