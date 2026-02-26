using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ai.AgentFramwork.Massar.Web.Tools;

public sealed class DocumentEditTool
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AuthenticationStateProvider _auth;
    private readonly IUnifiedDocumentSearchService _search;
    public const string DefaultEmbeddingModel = "text-embedding-3-large";

    public DocumentEditTool(
        IDbContextFactory<AppDbContext> dbFactory,
        AuthenticationStateProvider auth,
        IUnifiedDocumentSearchService search)
    {
        _dbFactory = dbFactory;
        _auth = auth;
        _search = search;
    }

    public async Task<string> EditDocumentAsync(
        Guid conversationId,
        string query,
        string editInstruction,
        int topK = 1,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return JsonSerializer.Serialize(new { Text = "Please enter a document to edit.", Attachments = Array.Empty<object>() });
        }

        var hits = await _search.SearchScopedAsync(query, conversationId, DefaultEmbeddingModel, topK, ct);
        if (hits.Count == 0)
        {
            return JsonSerializer.Serialize(new { Text = "No relevant documents found for editing.", Attachments = Array.Empty<object>() });
        }

        // Only consider scoped attachments
        var scopedHit = hits.FirstOrDefault(h => h.Store == DocStore.Scoped && h.AttachmentId != null);
        if (scopedHit == null)
        {
            return JsonSerializer.Serialize(new { Text = "No scoped document found for editing.", Attachments = Array.Empty<object>() });
        }

        // Load the document bytes from the database/blob storage
        byte[]? originalDocBytes = null;
        string? title = scopedHit.Title;

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            // Prefer the exact attachment that search returned (scopedHit.AttachmentId).
            // Fallback to latest blob in conversation if needed.
            ChatAttachmentBlob? blob = null;

            if (scopedHit.AttachmentId.HasValue)
            {
                blob = await db.ChatAttachmentBlobs
                    .AsNoTracking()
                    .Where(b => b.AttachmentId == scopedHit.AttachmentId.Value)
                    .FirstOrDefaultAsync(ct);
            }

            if (blob == null)
            {
                blob = await (
                    from a in db.ChatConversations
                    join b in db.ChatMessages on a.ConversationId equals b.ConversationId
                    join c in db.ChatMessageAttachments on b.MessageId equals c.MessageId
                    join d in db.ChatAttachmentBlobs on c.AttachmentId equals d.AttachmentId
                    where a.ConversationId == conversationId
                    orderby d.CreatedUtc descending
                    select d
                ).FirstOrDefaultAsync(ct);
            }

            if (blob == null || blob.FileContent == null || blob.FileContent.Length == 0)
                return JsonSerializer.Serialize(new { Text = "Attachment not found or has no content.", Attachments = Array.Empty<object>() });

            originalDocBytes = blob.FileContent;
        }

        // Edit the document in-memory
        var editedDocBytes = AddCommentToWordDocumentInMemory(originalDocBytes, editInstruction);

        // PATCH: store edited bytes back to ChatAttachmentBlob and return an API lookup attachment
        var editedAttachmentId = Guid.NewGuid();
        string editedFileName = string.IsNullOrWhiteSpace(title) ? "edited-document.docx" : title;
        if (!editedFileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            editedFileName += ".docx";

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            db.ChatAttachmentBlobs.Add(new ChatAttachmentBlob
            {
                AttachmentId = editedAttachmentId,
                FileName = editedFileName,
                ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                FileSizeBytes = editedDocBytes.Length,
                FileContent = editedDocBytes,
                CreatedUtc = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);
        }

        // PATCH: return the SAME attachment descriptor shape as DocumentSearchTool:
        // { Store: <Global|Scoped>, Id: "<guid>", FileName: "..." }
        // We return Scoped because it is a chat attachment accessible via /api/chat/attachments/<id>
        var attachments = new[]
        {
            new
            {
                Store = DocStore.Scoped,
                Id = editedAttachmentId.ToString(),
                FileName = editedFileName
            }
        };

        var text = "Edit applied successfully. I attached the edited document.";
        return JsonSerializer.Serialize(new { Text = text, Attachments = attachments });
    }

    private byte[] AddCommentToWordDocumentInMemory(byte[] docBytes, string commentText)
    {
        using var memStream = new MemoryStream();
        memStream.Write(docBytes, 0, docBytes.Length);
        memStream.Position = 0;

        using (var wordDoc = WordprocessingDocument.Open(memStream, true))
        {
            var mainPart = wordDoc.MainDocumentPart;
            if (mainPart == null) return docBytes;

            var commentsPart = mainPart.GetPartsOfType<WordprocessingCommentsPart>().FirstOrDefault()
                ?? mainPart.AddNewPart<WordprocessingCommentsPart>();
            if (commentsPart.Comments == null)
                commentsPart.Comments = new Comments();

            var commentId = (commentsPart.Comments.Elements<Comment>().Count() + 1).ToString();
            var comment = new Comment()
            {
                Id = commentId,
                Author = "Agent",
                Date = DateTime.Now
            };
            comment.AppendChild(new Paragraph(new Run(new Text(commentText))));
            commentsPart.Comments.AppendChild(comment);

            var para = mainPart.Document.Body.Elements<Paragraph>().FirstOrDefault();
            if (para != null)
            {
                // Avoid exceptions if paragraph has no runs
                var firstRun = para.Elements<Run>().FirstOrDefault();
                var lastRun = para.Elements<Run>().LastOrDefault();

                var commentRangeStart = new CommentRangeStart() { Id = commentId };
                var commentRangeEnd = new CommentRangeEnd() { Id = commentId };

                if (firstRun != null)
                    para.InsertBefore(commentRangeStart, firstRun);
                else
                    para.PrependChild(commentRangeStart);

                if (lastRun != null)
                    para.InsertAfter(commentRangeEnd, lastRun);
                else
                    para.AppendChild(commentRangeEnd);

                para.AppendChild(new Run(new CommentReference() { Id = commentId }));
            }

            mainPart.Document.Save();
        }
        return memStream.ToArray();
    }

    private async Task<int[]> GetRoleIdsAsync(CancellationToken ct = default)
    {
        var state = await _auth.GetAuthenticationStateAsync();
        var user = state.User;
        return user.Claims
            .Where(c => c.Type is "roleId" or "RoleId" or System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value)
            .Select(v => int.TryParse(v, out var i) ? (int?)i : null)
            .Where(i => i.HasValue)
            .Select(i => i!.Value)
            .Distinct()
            .ToArray();
    }
}