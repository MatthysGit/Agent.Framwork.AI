namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

public sealed class ChatConversationAttachmentIngestService : IChatConversationAttachmentIngestService
{
    private readonly AppDbContext _db;
    private readonly IDocumentTextExtractor _extractor;
    private readonly ITextChunker _chunker;
    private readonly IEmbeddingService _embedding;
    private readonly IPdfOcrService _pdfOcr;
    private readonly ILogger<ChatConversationAttachmentIngestService> _logger;

    // Guardrails (tune as needed)
    private const int MaxTabularRowsPerSheet = 5000;      // prevent massive sheets
    private const int MaxCharsPerChunk = 3500;            // keep embeddings focused
    private const int MaxCharsSummary = 12000;            // summary chunk cap
    private const int MaxChunksPerAttachment = 250;       // prevent runaway
    private const int MinUsefulChunkChars = 30;           // skip tiny chunks

    public ChatConversationAttachmentIngestService(
        AppDbContext db,
        IDocumentTextExtractor extractor,
        ITextChunker chunker,
        IEmbeddingService embedding,
        IPdfOcrService pdfOcr,
        ILogger<ChatConversationAttachmentIngestService> logger)
    {
        _db = db;
        _extractor = extractor;
        _chunker = chunker;
        _embedding = embedding;
        _pdfOcr = pdfOcr;
        _logger = logger;
    }

    public async Task IngestAsync(
        Guid conversationId,
        Guid attachmentId,
        string fileName,
        string contentType,
        byte[] bytes,
        CancellationToken ct = default)
    {
        if (conversationId == Guid.Empty) throw new InvalidOperationException("ConversationId is required.");
        if (attachmentId == Guid.Empty) throw new InvalidOperationException("AttachmentId is required.");
        if (bytes is null || bytes.Length == 0) throw new InvalidOperationException("Attachment content is empty.");

        contentType ??= "application/octet-stream";
        fileName ??= "attachment.bin";

        var hash = SHA256.HashData(bytes);

        var existingIngestId = await _db.ChatConversationAttachmentIngests
            .AsNoTracking()
            .Where(x => x.ConversationId == conversationId && x.AttachmentId == attachmentId)
            .Select(x => x.ChatConversationAttachmentIngestId)
            .FirstOrDefaultAsync(ct);

        if (existingIngestId != Guid.Empty)
        {
            _logger.LogInformation(
                "Chat attachment already ingested. ConversationId={ConversationId} AttachmentId={AttachmentId}",
                conversationId, attachmentId);
            return;
        }

        // Extract text (primary)
        var usedOcr = false;
        var text = await _extractor.ExtractAsync(bytes, contentType, fileName);

        // OCR fallback for scanned PDFs
        if (string.IsNullOrWhiteSpace(text) && IsPdf(fileName, contentType))
        {
            _logger.LogWarning(
                "Chat attachment primary extraction produced no text. Using OCR. AttachmentId={AttachmentId} File={File}",
                attachmentId, fileName);

            usedOcr = true;
            text = await _pdfOcr.ExtractTextAsync(bytes);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            var failedId = Guid.NewGuid();
            _db.ChatConversationAttachmentIngests.Add(new ChatConversationAttachmentIngest
            {
                ChatConversationAttachmentIngestId = failedId,
                ConversationId = conversationId,
                AttachmentId = attachmentId,
                FileName = fileName,
                ContentType = contentType,
                FileSizeBytes = bytes.LongLength,
                ContentHash = hash,
                UsedOcr = usedOcr,
                ExtractedText = null,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            await _db.SaveChangesAsync(ct);
            return;
        }

        // Create ingest header
        var ingestId = Guid.NewGuid();

        _db.ChatConversationAttachmentIngests.Add(new ChatConversationAttachmentIngest
        {
            ChatConversationAttachmentIngestId = ingestId,
            ConversationId = conversationId,
            AttachmentId = attachmentId,
            FileName = fileName,
            ContentType = contentType,
            FileSizeBytes = bytes.LongLength,
            ContentHash = hash,
            UsedOcr = usedOcr,
            ExtractedText = text,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });

        // ✅ Initialize chunks up-front so it is always assigned
        List<string> chunks = new();

        if (TabularIngestHelper.IsTabular(fileName, contentType))
        {
            try
            {
                var sheets = TabularIngestHelper.ParseTabular(bytes, fileName, contentType);

                // Cap rows per sheet (defensive)
                for (int s = 0; s < sheets.Count; s++)
                {
                    var rows = sheets[s].Rows;
                    if (rows.Count > MaxTabularRowsPerSheet)
                        sheets[s] = sheets[s] with { Rows = rows.Take(MaxTabularRowsPerSheet).ToList() };
                }

                var summary = TabularIngestHelper.SummarizeTables(sheets);

                if (!string.IsNullOrWhiteSpace(summary) && summary.Length > MaxCharsSummary)
                    summary = summary.Substring(0, MaxCharsSummary) + "…";

                var sheetChunks = TabularIngestHelper.SheetAwareChunk(
                    sheets,
                    maxChars: MaxCharsPerChunk,
                    overlapRows: 2);

                _logger.LogInformation(
                    "Tabular ingest parsed. AttachmentId={AttachmentId} Sheets={Sheets} SummaryChars={SummaryChars} SheetChunks={ChunkCount}",
                    attachmentId, sheets.Count, summary?.Length ?? 0, sheetChunks.Count);

                // Put summary first
                if (!string.IsNullOrWhiteSpace(summary))
                    chunks.Add(summary);

                chunks.AddRange(sheetChunks);

                // If tabular parsing yielded nothing, fall back
                if (chunks.Count == 0)
                {
                    _logger.LogWarning("Tabular parsing returned no chunks. Falling back to normal chunker. AttachmentId={AttachmentId}", attachmentId);
                    chunks = _chunker.Split(text, maxChars: 4000, overlapChars: 400);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tabular ingest failed. Falling back to normal chunker. AttachmentId={AttachmentId} File={FileName}",
                    attachmentId, fileName);

                // ✅ Fallback: normal chunking from extracted text
                chunks = _chunker.Split(text, maxChars: 4000, overlapChars: 400);
            }
        }
        else
        {
            chunks = _chunker.Split(text, maxChars: 4000, overlapChars: 400);
        }

        // Guard: prevent runaway + skip tiny chunks
        chunks = chunks
            .Where(c => !string.IsNullOrWhiteSpace(c) && c.Trim().Length >= MinUsefulChunkChars)
            .Take(MaxChunksPerAttachment)
            .ToList();

        // If still no chunks, store a single minimal chunk
        if (chunks.Count == 0)
        {
            chunks.Add($"(No chunkable content extracted from {fileName}.)");
        }

        // Embed + store
        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var chunkText = chunks[i].Trim();
            var chunkId = Guid.NewGuid();

            _db.ChatConversationAttachmentChunks.Add(new ChatConversationAttachmentChunk
            {
                ChatConversationAttachmentChunkId = chunkId,
                ChatConversationAttachmentIngestId = ingestId,
                ChunkIndex = i,
                Text = chunkText,
                CreatedUtc = DateTime.UtcNow
            });

            var (model, vector) = await _embedding.GenerateAsync(chunkText);

            _db.ChatConversationAttachmentChunkEmbeddings.Add(new ChatConversationAttachmentChunkEmbedding
            {
                ChatConversationAttachmentChunkId = chunkId,
                EmbeddingModel = model,
                Dimensions = vector.Length,
                VectorBinary = FloatArrayToBytes(vector),
                CreatedUtc = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Chat attachment ingested. ConversationId={ConversationId} AttachmentId={AttachmentId} Chunks={Chunks}",
            conversationId, attachmentId, chunks.Count);
    }

    private static bool IsPdf(string fileName, string contentType)
    {
        if (string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = Path.GetExtension(fileName);
        return string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] FloatArrayToBytes(float[] array)
    {
        var bytes = new byte[array.Length * sizeof(float)];
        Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}