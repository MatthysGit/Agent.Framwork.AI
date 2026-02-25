using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Services.Documents;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel;

namespace Ai.AgentFramwork.Massar.Web.Tools;

public sealed class ChatAttachmentReaderTool
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ChatSession _session;
    private readonly ChatAttachmentStore _attachmentStore;
    private readonly IDocumentTextExtractor _extractor;

    public ChatAttachmentReaderTool(
        IDbContextFactory<AppDbContext> dbFactory,
        ChatSession session,
        ChatAttachmentStore attachmentStore,
        IDocumentTextExtractor extractor)
    {
        _dbFactory = dbFactory;
        _session = session;
        _attachmentStore = attachmentStore;
        _extractor = extractor;
    }

    [Description("Reads the latest uploaded chat attachment (optionally filtered by filename) and returns extracted text.")]
    public async Task<string> GetChatAttachmentTextAsync(
        [Description("Optional filename filter, e.g. Corporate_Data_Security_Policy_Sample.docx")] string? fileName = null)
    {
        if (!_session.ActiveConversationId.HasValue)
            return "No active conversation.";

        await using var db = await _dbFactory.CreateDbContextAsync();

        var q =
            from a in db.ChatMessageAttachments.AsNoTracking()
            join m in db.ChatMessages.AsNoTracking() on a.MessageId equals m.MessageId
            where m.ConversationId == _session.ActiveConversationId.Value
                  && !a.IsFromAgent
            select a;

        if (!string.IsNullOrWhiteSpace(fileName))
            q = q.Where(a => a.FileName == fileName);

        var att = await q.OrderByDescending(a => a.CreatedUtc).FirstOrDefaultAsync();
        if (att is null)
            return "No uploaded file found in this conversation.";

        var path = _attachmentStore.TryResolveStoredPath(att.AttachmentId);
        if (path is null || !File.Exists(path))
            return "Uploaded file exists but cannot be accessed from storage.";

        var bytes = await File.ReadAllBytesAsync(path);

        var text = await _extractor.ExtractAsync(bytes, att.MimeType ?? "", att.FileName);
        if (string.IsNullOrWhiteSpace(text))
            return $"The file \"{att.FileName}\" is empty or contains no searchable content.";

        // Keep response manageable
        if (text.Length > 12000) text = text.Substring(0, 12000) + "\n…(truncated)";
        return text;
    }
}