using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.DTO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

public sealed class ChatConversationRepository : IChatConversationRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ChatConversationRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<Guid> EnsureConversationAsync(string userId, string? title, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var convo = new ChatConversation
        {
            ConversationId = Guid.NewGuid(),
            UserId = userId,
            Title = title,
            IsArchived = false
        };

        db.ChatConversations.Add(convo);
        await db.SaveChangesAsync(ct);

        return convo.ConversationId;
    }

    public async Task<Guid> AppendMessageAsync(
        Guid conversationId,
        string senderRole,
        string content,
        string contentType,
        string? metadataJson,
        IEnumerable<ChatAttachmentInfo>? attachments,
        CancellationToken ct = default)
    {
        // Ignore UI cancellation for persistence if caller passes a cancelled token by mistake.
        var dbCt = CancellationToken.None;

        await using var db = await _dbFactory.CreateDbContextAsync(dbCt);

        // ✅ Required when SQL Server retry strategy is enabled AND you use explicit transactions.
        var strategy = db.Database.CreateExecutionStrategy();

        Guid messageId = Guid.Empty;

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, dbCt);

            var nextSeq = (await db.ChatMessages
                .Where(m => m.ConversationId == conversationId)
                .MaxAsync(m => (int?)m.SequenceNo, dbCt) ?? 0) + 1;

            var msg = new Ai.AgentFramwork.Massar.Web.DBModels.ChatMessage
            {
                MessageId = Guid.NewGuid(),
                ConversationId = conversationId,
                SenderRole = senderRole,
                SequenceNo = nextSeq,
                ContentType = contentType,
                Content = content,
                MetadataJson = metadataJson
            };

            db.ChatMessages.Add(msg);

            if (attachments is not null)
            {
                // Normalize & dedupe in-memory first
                var list = attachments
                    .Where(a => a.AttachmentId != Guid.Empty)
                    .GroupBy(a => a.AttachmentId)
                    .Select(g => g.First())
                    .ToList();

                if (list.Count > 0)
                {
                    // Avoid duplicate insert against UQ_ChatMessageAttachment_AttachmentId
                    var ids = list.Select(a => a.AttachmentId).ToList();

                    var alreadyLinked = await db.ChatMessageAttachments
                        .AsNoTracking()
                        .Where(x => ids.Contains(x.AttachmentId))
                        .Select(x => x.AttachmentId)
                        .ToListAsync(dbCt);

                    var already = new HashSet<Guid>(alreadyLinked);

                    foreach (var a in list)
                    {
                        if (already.Contains(a.AttachmentId))
                            continue; // ✅ skip duplicate attachmentId

                        db.ChatMessageAttachments.Add(new ChatMessageAttachment
                        {
                            AttachmentId = a.AttachmentId,
                            MessageId = msg.MessageId,
                            FileName = a.FileName,
                            MimeType = a.MimeType,
                            StorageUrl = a.StorageUrl,
                            SizeBytes = a.SizeBytes
                        });
                    }
                }
            }

            var convo = await db.ChatConversations
                .FirstAsync(c => c.ConversationId == conversationId, dbCt);

            convo.UpdatedUtc = DateTime.UtcNow;

            await db.SaveChangesAsync(dbCt);
            await tx.CommitAsync(dbCt);

            messageId = msg.MessageId;
        });

        return messageId;
    }

    public async Task<List<ConversationListItem>> GetUserConversationsAsync(
        string userId,
        bool includeArchived = false,
        int take = 50,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var q = db.ChatConversations.Where(c => c.UserId == userId);

        if (!includeArchived)
            q = q.Where(c => !c.IsArchived);

        return await q
            .OrderByDescending(c => c.UpdatedUtc)
            .Take(take)
            .Select(c => new ConversationListItem
            {
                ConversationId = c.ConversationId,
                Title = c.Title,
                CreatedUtc = c.CreatedUtc,
                UpdatedUtc = c.UpdatedUtc,
                IsArchived = c.IsArchived,
                MessageCount = db.ChatMessages.Count(m => m.ConversationId == c.ConversationId)
            })
            .ToListAsync(ct);
    }

    public async Task<(List<ChatMessage> Messages, Guid ConversationId)> LoadConversationAsync(
      string userId,
      Guid conversationId,
      Func<string, IEnumerable<ChatAttachmentInfo>, string> appendAttachmentLinks,
      CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var convoExists = await db.ChatConversations
            .AnyAsync(c => c.ConversationId == conversationId && c.UserId == userId, ct);

        if (!convoExists)
            throw new InvalidOperationException("Conversation not found or access denied.");

        var dbMessages = await db.ChatMessages
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.SequenceNo)
            .ToListAsync(ct);

        var msgIds = dbMessages.Select(m => m.MessageId).ToList();

        var atts = await db.ChatMessageAttachments
            .Where(a => msgIds.Contains(a.MessageId))
            .OrderBy(a => a.CreatedUtc)
            .ToListAsync(ct);

        var attLookup = atts
            .GroupBy(a => a.MessageId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => new ChatAttachmentInfo(a.AttachmentId, a.FileName, a.MimeType, a.StorageUrl, a.SizeBytes)).ToList());

        static ChatRole MapRole(string senderRole) => senderRole switch
        {
            "user" => ChatRole.User,
            "assistant" => ChatRole.Assistant,
            "system" => ChatRole.System,
            "tool" => ChatRole.Tool,
            _ => ChatRole.Assistant
        };

        // PATCH: strip any previously-saved attachments block from m.Content to prevent duplication.
        static string StripSavedAttachmentsBlock(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return string.Empty;

            // We only want to remove the UI-rendered attachments section that your system appends.
            // It usually starts with "**Attachments**" on its own line.
            // We’ll remove from the FIRST occurrence to the end of the message.
            var idx = content.IndexOf("**Attachments**", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return content;

            // If attachments header is at the top or later, strip it and anything after it.
            return content.Substring(0, idx).TrimEnd();
        }

        var list = new List<ChatMessage>();

        foreach (var m in dbMessages)
        {
            var msgAtts = attLookup.TryGetValue(m.MessageId, out var listAtts)
                ? (IEnumerable<ChatAttachmentInfo>)listAtts
                : Array.Empty<ChatAttachmentInfo>();

            var baseContent = StripSavedAttachmentsBlock(m.Content ?? string.Empty);

            // Only append attachments once (from ChatMessageAttachments)
            var text = appendAttachmentLinks(baseContent, msgAtts);

            list.Add(new ChatMessage(MapRole(m.SenderRole), text));
        }

        return (list, conversationId);
    }

    public async Task RenameConversationAsync(string userId, Guid conversationId, string? newTitle, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var convo = await db.ChatConversations
            .FirstOrDefaultAsync(c => c.ConversationId == conversationId && c.UserId == userId, ct);

        if (convo is null)
            throw new InvalidOperationException("Conversation not found.");

        convo.Title = string.IsNullOrWhiteSpace(newTitle) ? null : newTitle.Trim();
        convo.UpdatedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task SetArchiveStateAsync(string userId, Guid conversationId, bool isArchived, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var convo = await db.ChatConversations
            .FirstOrDefaultAsync(c => c.ConversationId == conversationId && c.UserId == userId, ct);

        if (convo is null)
            throw new InvalidOperationException("Conversation not found.");

        convo.IsArchived = isArchived;
        convo.UpdatedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteConversationAsync(string userId, Guid conversationId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var convoExists = await db.ChatConversations
                .AnyAsync(c => c.ConversationId == conversationId && c.UserId == userId, ct);

            if (!convoExists)
                return;

            // Collect AttachmentIds used by this conversation
            var ingestAttachmentIdsQuery =
                db.ChatConversationAttachmentIngests
                  .Where(i => i.ConversationId == conversationId)
                  .Select(i => i.AttachmentId);

            var messageAttachmentIdsQuery =
                db.ChatMessageAttachments
                  .Where(a => a.Message.ConversationId == conversationId)
                  .Select(a => a.AttachmentId);

            var attachmentIds = await ingestAttachmentIdsQuery
                .Union(messageAttachmentIdsQuery)
                .Distinct()
                .ToListAsync(ct);

            // ---- Delete conversation attachment graph (embeddings -> chunks -> ingests) ----

            await db.ChatConversationAttachmentChunkEmbeddings
                .Where(e =>
                    db.ChatConversationAttachmentChunks
                      .Where(ch =>
                          db.ChatConversationAttachmentIngests
                            .Where(i => i.ConversationId == conversationId)
                            .Select(i => i.ChatConversationAttachmentIngestId)
                            .Contains(ch.ChatConversationAttachmentIngestId))
                      .Select(ch => ch.ChatConversationAttachmentChunkId)
                      .Contains(e.ChatConversationAttachmentChunkId))
                .ExecuteDeleteAsync(ct);

            await db.ChatConversationAttachmentChunks
                .Where(ch =>
                    db.ChatConversationAttachmentIngests
                      .Where(i => i.ConversationId == conversationId)
                      .Select(i => i.ChatConversationAttachmentIngestId)
                      .Contains(ch.ChatConversationAttachmentIngestId))
                .ExecuteDeleteAsync(ct);

            await db.ChatConversationAttachmentIngests
                .Where(i => i.ConversationId == conversationId)
                .ExecuteDeleteAsync(ct);

            // ---- Delete messages and message attachments ----

            await db.ChatMessageAttachments
                .Where(a => a.Message.ConversationId == conversationId)
                .ExecuteDeleteAsync(ct);

            await db.ChatMessages
                .Where(m => m.ConversationId == conversationId)
                .ExecuteDeleteAsync(ct);




            // ---- Delete conversation ----

            await db.ChatConversations
                .Where(c => c.ConversationId == conversationId && c.UserId == userId)
                .ExecuteDeleteAsync(ct);

            // ---- Delete blobs/contents for attachments, only if they are not used elsewhere ----
            if (attachmentIds.Count > 0)
            {
                var stillUsedByOtherConversations =
                    db.ChatConversationAttachmentIngests
                      .Where(i => attachmentIds.Contains(i.AttachmentId))
                      .Select(i => i.AttachmentId);

                var stillUsedByOtherMessages =
                    db.ChatMessageAttachments
                      .Where(a => attachmentIds.Contains(a.AttachmentId))
                      .Select(a => a.AttachmentId);

                var stillUsed = stillUsedByOtherConversations.Union(stillUsedByOtherMessages);

                await db.ChatAttachmentContents
                    .Where(b => attachmentIds.Contains(b.AttachmentId) && !stillUsed.Contains(b.AttachmentId))
                    .ExecuteDeleteAsync(ct);

                await db.ChatAttachmentBlobs
                    .Where(b => attachmentIds.Contains(b.AttachmentId) && !stillUsed.Contains(b.AttachmentId))
                    .ExecuteDeleteAsync(ct);
            }

            await tx.CommitAsync(ct);
        });
    }
}
