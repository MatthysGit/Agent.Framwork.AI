using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Services.Documents;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

public sealed class ChatAttachmentStore
{
    private readonly IWebHostEnvironment _env;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IChatConversationAttachmentIngestService _chatIngest;

    public ChatAttachmentStore(
        IWebHostEnvironment env,
        IDbContextFactory<AppDbContext> dbFactory,
        IChatConversationAttachmentIngestService chatIngest)
    {
        Console.WriteLine(">>> ChatAttachmentStore constructed (DB version)");
        _env = env;
        _dbFactory = dbFactory;
        _chatIngest = chatIngest;
    }

    // Disk is no longer used, but keep env field in case other code needs it later.
    private string UploadsRoot => Path.Combine(_env.ContentRootPath, "App_Data", "ChatUploads");

    public string AppendAttachmentLinks(string content, IEnumerable<ChatAttachmentInfo> attachments)
    {
        var baseText = content ?? string.Empty;
        var list = attachments?.ToList() ?? new();
        if (list.Count == 0) return baseText;

        var lines = new List<string>
        {
            baseText.TrimEnd(),
            "",
            "**Attachments**"
        };

        foreach (var a in list)
            lines.Add($"- [{a.FileName}]({a.StorageUrl})");

        return string.Join("\n", lines).Trim();
    }

    /// <summary>
    /// Saves USER uploaded files to the database (dbo.ChatAttachmentBlob) AND ingests them into
    /// chat-temp tables for retrieval (ChatConversationAttachment*). No disk usage.
    /// </summary>
    public async Task<List<ChatAttachmentInfo>> SaveUploadedFilesAsync(
        Guid conversationId,
        IReadOnlyList<IBrowserFile> files,
        CancellationToken uiCt)
    {
        var saved = new List<ChatAttachmentInfo>();
        if (files is null || files.Count == 0) return saved;

        if (conversationId == Guid.Empty)
            throw new InvalidOperationException("ConversationId is required to store chat attachments.");

        foreach (var f in files)
        {
            uiCt.ThrowIfCancellationRequested();

            var attachmentId = Guid.NewGuid();
            var fileName = f.Name;
            var contentType = string.IsNullOrWhiteSpace(f.ContentType) ? "application/octet-stream" : f.ContentType;

            // Read bytes (do not cancel once read starts)
            byte[] bytes;
            await using (var input = f.OpenReadStream(maxAllowedSize: 50 * 1024 * 1024))
            using (var ms = new MemoryStream())
            {
                await input.CopyToAsync(ms, CancellationToken.None);
                bytes = ms.ToArray();
            }

            // 1) Persist raw blob
            await SaveBlobAsync(attachmentId, fileName, contentType, bytes);

            // 2) Ingest into chat-temp retrieval tables (NOT the normal Documents pipeline)
            //    If OCR/extraction fails, the ingest service should handle that gracefully.

            try
            {
                await _chatIngest.IngestAsync(
                    conversationId: conversationId,
                    attachmentId: attachmentId,
                    fileName: fileName,
                    contentType: contentType,
                    bytes: bytes,
                    ct: uiCt);
            }
            catch (Exception exception)
            {
                Console.WriteLine($">>> Chat ingest failed for {fileName}: {exception}");
            }
            


            var url = $"/api/chat/attachments/{attachmentId}";
            saved.Add(new ChatAttachmentInfo(
                attachmentId,
                fileName,
                contentType,
                url,
                bytes.LongLength));
        }

        return saved;
    }

    /// <summary>
    /// Saves ASSISTANT-generated files to the database (dbo.ChatAttachmentBlob). No disk usage.
    /// NOTE: This version does NOT ingest into chat-temp tables because it does not have ConversationId.
    /// </summary>
    public async Task<ChatAttachmentInfo> SaveAssistantFileAsync(string fileName, string contentType, byte[] bytes)
    {
        var attachmentId = Guid.NewGuid();

        var ct = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        var name = string.IsNullOrWhiteSpace(fileName) ? "attachment.bin" : fileName;
        bytes ??= Array.Empty<byte>();

        await SaveBlobAsync(attachmentId, name, ct, bytes);

        var url = $"/api/chat/attachments/{attachmentId}";
        return new ChatAttachmentInfo(
            attachmentId,
            name,
            ct,
            url,
            bytes.LongLength);
    }

    /// <summary>
    /// Optional overload if you DO want assistant files to be searchable in the same conversation.
    /// </summary>
    public async Task<ChatAttachmentInfo> SaveAssistantFileAsync(
        Guid conversationId,
        string fileName,
        string contentType,
        byte[] bytes,
        CancellationToken ct = default)
    {
        if (conversationId == Guid.Empty)
            throw new InvalidOperationException("ConversationId is required to store chat attachments.");

        var attachmentId = Guid.NewGuid();

        var ctype = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        var name = string.IsNullOrWhiteSpace(fileName) ? "attachment.bin" : fileName;
        bytes ??= Array.Empty<byte>();

        await SaveBlobAsync(attachmentId, name, ctype, bytes);

        await _chatIngest.IngestAsync(
            conversationId: conversationId,
            attachmentId: attachmentId,
            fileName: name,
            contentType: ctype,
            bytes: bytes,
            ct: ct);

        var url = $"/api/chat/attachments/{attachmentId}";
        return new ChatAttachmentInfo(
            attachmentId,
            name,
            ctype,
            url,
            bytes.LongLength);
    }

    private async Task SaveBlobAsync(Guid id, string fileName, string contentType, byte[] bytes)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Insert into dbo.ChatAttachmentBlob using raw SQL (DB-first safe, no entity required)
        const string sql = @"
INSERT INTO dbo.ChatAttachmentBlob (AttachmentId, FileName, ContentType, FileSizeBytes, FileContent, CreatedUtc)
VALUES (@p0, @p1, @p2, @p3, @p4, SYSUTCDATETIME());";

        await db.Database.ExecuteSqlRawAsync(
            sql,
            id,
            fileName,
            contentType,
            (long)bytes.LongLength,
            bytes);
    }

    /// <summary>
    /// No disk storage anymore, so always returns null.
    /// </summary>
    public string? TryResolveStoredPath(Guid attachmentId) => null;
}