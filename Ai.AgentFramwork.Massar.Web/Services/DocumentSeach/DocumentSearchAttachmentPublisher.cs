using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;
using Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

internal static class DocumentSearchAttachmentPublisher
{
    public static bool TryParse(string text, out DocumentSearchAgentResponse? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            payload = JsonSerializer.Deserialize<DocumentSearchAgentResponse>(text);
            return payload != null && payload.Attachments != null;
        }
        catch
        {
            return false;
        }
    }

    public static async Task PublishAsync(
        AppDbContext db,
        Guid messageId,
        int[] roleIds,
        DocumentSearchAgentResponse payload,
        CancellationToken ct = default)
    {
        // Attach scoped directly; global must be copied into ChatAttachmentBlob
        foreach (var a in payload.Attachments.Take(3))
        {
            if (a.Store == DocStore.Scoped)
            {
                // link existing scoped blob
                var attachmentId = a.Id;

                db.ChatMessageAttachments.Add(new ChatMessageAttachment
                {
                    MessageId = messageId,
                    AttachmentId = attachmentId,
                    FileName = a.FileName,
                    StorageUrl = $"/api/chat/attachments/{attachmentId}",
                    IsFromAgent = true
                });

                continue;
            }

            // Global: enforce CanDownload
            var documentFileId = a.Id;

            var file = await db.DocumentFiles
                .AsNoTracking()
                .Where(f => f.DocumentFileId == documentFileId)
                .Select(f => new { f.DocumentId, f.FileName, f.ContentType, f.FileSizeBytes, f.FileContent })
                .FirstOrDefaultAsync(ct);

            if (file is null) continue;

            var canDownload = await db.DocumentRoleAccesses.AnyAsync(x =>
                x.IsActive &&
                x.CanDownload &&
                file.DocumentId == x.DocumentId &&
                roleIds.Contains(x.RoleId), ct);

            if (!canDownload) continue;

            var newAttachmentId = Guid.NewGuid();

            db.ChatAttachmentBlobs.Add(new ChatAttachmentBlob
            {
                AttachmentId = newAttachmentId,
                FileName = file.FileName,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                FileSizeBytes = file.FileSizeBytes,
                FileContent = file.FileContent,
                CreatedUtc = DateTime.UtcNow
            });

            db.ChatMessageAttachments.Add(new ChatMessageAttachment
            {
                MessageId = messageId,
                AttachmentId = newAttachmentId,
                FileName = file.FileName,
                MimeType = file.ContentType,
                StorageUrl = $"/api/chat/attachments/{newAttachmentId}",
                IsFromAgent = true
            });
        }
    }
}