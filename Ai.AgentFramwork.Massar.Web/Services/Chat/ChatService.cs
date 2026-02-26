using Ai.AgentFramwork.Massar.Web.Components.Pages.Chat;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.DTO;
using Ai.AgentFramwork.Massar.Web.Models;
using Ai.AgentFramwork.Massar.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using System.Collections;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

public sealed class ChatService
{
    private readonly AuthenticationStateProvider _auth;
    private readonly ChatSession _session;
    private readonly IChatConversationRepository _repo;
    private readonly ChatAttachmentStore _attachments;
    private readonly AIAgent _orchestratorAgent;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly DocumentSearchTool _docSearchTool;
    private readonly DocumentEditTool _docEditTool;

    private ChatSuggestions? _chatSuggestions;

    public string? CurrentAgentName { get; private set; }

    public event Action? OnMessagesUpdated
    {
        add => _session.OnMessagesUpdated += value;
        remove => _session.OnMessagesUpdated -= value;
    }

    public Guid? ActiveConversationId => _session.ActiveConversationId;
    public IReadOnlyList<ChatMessage> Messages => _session.Messages;

    public ChatService(
        AuthenticationStateProvider auth,
        ChatSession session,
        IChatConversationRepository repo,
        ChatAttachmentStore attachments,
        ChatAgentFactory agentFactory,
        ChatTools tools,
        IDbContextFactory<AppDbContext> dbFactory,
        DocumentSearchTool docSearchTool,
        DocumentEditTool docEditTool)
    {
        _auth = auth;
        _session = session;
        _repo = repo;
        _attachments = attachments;
        _dbFactory = dbFactory;
        _docSearchTool = docSearchTool;
        _docEditTool = docEditTool;

        _orchestratorAgent = agentFactory.BuildOrchestratorAgent(
            tools,
            session,
            dbFactory,
            auth);
    }

    private enum DocStore
    {
        Global = 1,
        Scoped = 2
    }

    private sealed record DocumentAttachmentDescriptor(
        [property: JsonPropertyName("Store")] JsonElement Store,
        [property: JsonPropertyName("Id")] string Id,
        [property: JsonPropertyName("FileName")] string FileName
    )
    {
        public DocStore StoreValue
        {
            get
            {
                if (Store.ValueKind == JsonValueKind.Number && Store.TryGetInt32(out var n))
                    return (DocStore)n;

                if (Store.ValueKind == JsonValueKind.String &&
                    Enum.TryParse<DocStore>(Store.GetString(), ignoreCase: true, out var e))
                    return e;

                return DocStore.Global;
            }
        }
    }

    private sealed record DocumentSearchAgentResponse(
        [property: JsonPropertyName("Text")] string Text,
        [property: JsonPropertyName("Attachments")] DocumentAttachmentDescriptor[] Attachments
    );

    private static readonly JsonSerializerOptions _docJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static bool TryParseDocSearchJson(string text, out DocumentSearchAgentResponse? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            payload = JsonSerializer.Deserialize<DocumentSearchAgentResponse>(text, _docJsonOptions);
            return payload is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeDocumentQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.ToLowerInvariant();

        if (t.Contains("/api/chat/attachments/") || t.Contains("/documents/files/download/"))
            return true;

        if (t.Contains(".pdf") || t.Contains(".docx") || t.Contains(".doc") || t.Contains(".txt") ||
            t.Contains(".csv") || t.Contains(".xlsx") || t.Contains(".xls"))
            return true;

        if (t.Contains("according to") || t.Contains("in the document") || t.Contains("in the pdf") ||
            t.Contains("from the file") || t.Contains("what is included") || t.Contains("what does it say") ||
            t.Contains("summarize") || t.Contains("quote") || t.Contains("cite"))
            return true;

        return false;
    }

    private static bool IsDocumentEditIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.ToLowerInvariant();

        return t.Contains("add comment")
               || t.Contains("add comments")
               || t.Contains("comment on")
               || t.Contains("review the document")
               || t.Contains("review this document")
               || t.Contains("review")
               || t.Contains("annotate")
               || t.Contains("highlight")
               || t.Contains("track changes")
               || t.Contains("suggest changes")
               || t.Contains("edit the document")
               || t.Contains("proofread")
               || t.Contains("revise");
    }

    private async Task<int[]> GetRoleIdsAsync(CancellationToken ct = default)
    {
        var state = await _auth.GetAuthenticationStateAsync();
        var user = state.User;

        var fromClaims = user.Claims
            .Where(c => c.Type is "roleId" or "RoleId" or ClaimTypes.Role)
            .Select(c => c.Value)
            .Select(v => int.TryParse(v, out var i) ? (int?)i : null)
            .Where(i => i != null)
            .Select(i => i!.Value)
            .Distinct()
            .ToArray();

        if (fromClaims.Length > 0)
            return fromClaims;

        var userIdStr = user.FindFirstValue(AppClaimTypes.UserId) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId))
            return Array.Empty<int>();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var roleId = await db.AppUsers
            .AsNoTracking()
            .Where(u => u.UserId.ToString() == userId.ToString())
            .Select(u => u.RoleId)
            .FirstOrDefaultAsync(ct);

        return roleId == 0 ? Array.Empty<int>() : (int[])new[] { roleId.Value };
    }

    private async Task<IReadOnlyList<object>> MaterializeDocSearchAttachmentsAsync(
        Type attachmentType,
        DocumentAttachmentDescriptor[] descriptors,
        int[] roleIds,
        CancellationToken ct = default)
    {
        var result = new List<object>();
        if (descriptors is null || descriptors.Length == 0)
            return result;

        var pAttachmentId = attachmentType.GetProperty("AttachmentId", BindingFlags.Public | BindingFlags.Instance);
        var pFileName = attachmentType.GetProperty("FileName", BindingFlags.Public | BindingFlags.Instance);
        var pMimeType = attachmentType.GetProperty("MimeType", BindingFlags.Public | BindingFlags.Instance);
        var pStorageUrl = attachmentType.GetProperty("StorageUrl", BindingFlags.Public | BindingFlags.Instance);

        if (pAttachmentId is null || pFileName is null || pMimeType is null || pStorageUrl is null)
            throw new InvalidOperationException(
                $"Attachment type '{attachmentType.Name}' must have settable properties: AttachmentId, FileName, MimeType, StorageUrl.");

        object NewAttachment(Guid id, string fileName, string mimeType)
        {
            var a = Activator.CreateInstance(attachmentType)
                ?? throw new InvalidOperationException(
                    $"Could not create attachment instance of '{attachmentType.Name}'. Ensure it has a public parameterless constructor.");

            pAttachmentId.SetValue(a, id);
            pFileName.SetValue(a, fileName);
            pMimeType.SetValue(a, mimeType);
            pStorageUrl.SetValue(a, $"/api/chat/attachments/{id}");
            return a;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        foreach (var d in descriptors.Take(3))
        {
            if (!Guid.TryParse(d.Id, out var parsedId))
                continue;

            if (d.StoreValue == DocStore.Scoped)
            {
                result.Add(NewAttachment(parsedId, d.FileName, "application/octet-stream"));
                continue;
            }

            var file = await db.DocumentFiles
                .AsNoTracking()
                .Where(f => f.DocumentFileId == parsedId)
                .Select(f => new { f.DocumentId, f.FileName, f.ContentType, f.FileSizeBytes, f.FileContent })
                .FirstOrDefaultAsync(ct);

            if (file is null) continue;

            var canDownload = await db.DocumentRoleAccesses.AnyAsync(a =>
                a.IsActive &&
                a.CanDownload &&
                a.DocumentId == file.DocumentId &&
                roleIds.Contains(a.RoleId), ct);

            if (!canDownload) continue;

            var newAttachmentId = Guid.NewGuid();

            db.ChatAttachmentBlobs.Add(new ChatAttachmentBlob
            {
                AttachmentId = newAttachmentId,
                FileName = string.IsNullOrWhiteSpace(file.FileName) ? d.FileName : file.FileName,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                FileSizeBytes = file.FileSizeBytes,
                FileContent = file.FileContent,
                CreatedUtc = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);

            result.Add(NewAttachment(
                newAttachmentId,
                string.IsNullOrWhiteSpace(file.FileName) ? d.FileName : file.FileName,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType));
        }

        return result;
    }

    public void StartNewChat() => _session.StartNewChat();
    public void StartNewConversation() => _session.StartNewChat();

    public async Task<string?> GetMyUserIdAsync()
    {
        var state = await _auth.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(AppClaimTypes.UserId);
    }

    public async Task<List<ConversationListItem>> GetUserConversationsAsync(bool includeArchived = false, int take = 50, CancellationToken ct = default)
    {
        var userId = await GetMyUserIdAsync();
        if (string.IsNullOrWhiteSpace(userId)) return new();
        return await _repo.GetUserConversationsAsync(userId, includeArchived, take, ct);
    }

    public async Task LoadConversationAsync(Guid conversationId, CancellationToken ct = default)
    {
        var userId = await GetMyUserIdAsync();
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("User is not authenticated.");

        var (msgs, _) = await _repo.LoadConversationAsync(userId, conversationId, _attachments.AppendAttachmentLinks, ct);

        _session.SetActiveConversation(conversationId);
        _session.ReplaceMessages(msgs);
    }

    public async Task RenameConversationAsync(Guid conversationId, string? newTitle, CancellationToken ct = default)
    {
        var userId = await GetMyUserIdAsync();
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("User is not authenticated.");

        await _repo.RenameConversationAsync(userId, conversationId, newTitle, ct);
    }

    public async Task ArchiveConversationAsync(Guid conversationId, CancellationToken ct = default)
    {
        var userId = await GetMyUserIdAsync();
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("User is not authenticated.");

        await _repo.SetArchiveStateAsync(userId, conversationId, isArchived: true, ct);

        if (_session.ActiveConversationId == conversationId)
            _session.StartNewChat();
    }

    public async Task UnarchiveConversationAsync(Guid conversationId, CancellationToken ct = default)
    {
        var userId = await GetMyUserIdAsync();
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("User is not authenticated.");

        await _repo.SetArchiveStateAsync(userId, conversationId, isArchived: false, ct);
    }

    public async Task DeleteConversationAsync(Guid conversationId, CancellationToken ct = default)
    {
        var userId = await GetMyUserIdAsync();
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("User is not authenticated.");

        await _repo.DeleteConversationAsync(userId, conversationId, ct);

        if (_session.ActiveConversationId == conversationId)
            _session.StartNewChat();
    }

    public async IAsyncEnumerable<IEnumerable<ChatMessage>> SendAsyc(ChatMessage userMessage)
    {
        await foreach (var msgs in SendAsyc(userMessage, Array.Empty<IBrowserFile>()))
            yield return msgs;
    }

    public async IAsyncEnumerable<IEnumerable<ChatMessage>> SendAsyc(ChatMessage userMessage, IReadOnlyList<IBrowserFile> files)
    {
        var streamCt = _session.BeginNewStreamingTurn();
        var persistCt = CancellationToken.None;

        var userId = await GetMyUserIdAsync();
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("User is not authenticated.");

        if (!_session.ActiveConversationId.HasValue)
        {
            var convoId = await _repo.EnsureConversationAsync(userId, ExtractTitleFrom(userMessage), persistCt);
            _session.SetActiveConversation(convoId);
        }

        var conversationId = _session.ActiveConversationId!.Value;

        var userAttachments = await _attachments.SaveUploadedFilesAsync(conversationId, files, streamCt);
        var userText = _attachments.AppendAttachmentLinks(GetText(userMessage), userAttachments);

        _session.AddMessage(new ChatMessage(ChatRole.User, userText));

        await _repo.AppendMessageAsync(
            conversationId,
            senderRole: "user",
            content: userText,
            contentType: "text/markdown",
            metadataJson: null,
            attachments: userAttachments,
            ct: persistCt);

        _session.ClearAssistantAttachments();
        var responseText = new TextContent("");
        var inProgress = new ChatMessage(ChatRole.Assistant, new[] { responseText });
        _session.SetStreamingMessage(inProgress);

        var rawUserQuery = StripMarkdownLinks(GetText(userMessage));

        // ✅ PATCH: EDIT path now parses JSON + materializes attachments + appends attachment links
        if (IsDocumentEditIntent(rawUserQuery) || IsDocumentEditIntent(userText))
        {
            _session.ClearStreamingMessage();

            var editInstruction = "add comments";
            var lower = rawUserQuery.ToLowerInvariant();
            if (lower.Contains("comment"))
                editInstruction = "add comments";
            else if (lower.Contains("annotate"))
                editInstruction = "annotate with comments";
            else if (lower.Contains("highlight"))
                editInstruction = "highlight issues and add comments";
            else if (lower.Contains("proofread"))
                editInstruction = "proofread and add comments";
            else if (lower.Contains("revise") || lower.Contains("edit"))
                editInstruction = "make suggested edits and add comments";

            var json = await _docEditTool.EditDocumentAsync(conversationId, rawUserQuery, editInstruction);

            if (TryParseDocSearchJson(json, out var docPayload) && docPayload is not null)
            {
                var roleIds = await GetRoleIdsAsync(persistCt);

                var listObj = (object)_session.CreatedAssistantAttachments;
                var listType = listObj.GetType();
                var attachmentType = listType.IsGenericType
                    ? listType.GetGenericArguments()[0]
                    : throw new InvalidOperationException("CreatedAssistantAttachments must be List<T>.");

                var created = await MaterializeDocSearchAttachmentsAsync(
                    attachmentType,
                    docPayload.Attachments ?? Array.Empty<DocumentAttachmentDescriptor>(),
                    roleIds,
                    persistCt);

                var asIList = (IList)listObj;
                foreach (var a in created)
                    asIList.Add(a);

                var assistantText2 = _attachments.AppendAttachmentLinks(
                    docPayload.Text ?? string.Empty,
                    _session.CreatedAssistantAttachments);

                _session.AddMessage(new ChatMessage(ChatRole.Assistant, assistantText2));

                await _repo.AppendMessageAsync(
                    conversationId,
                    senderRole: "assistant",
                    content: assistantText2,
                    contentType: "text/markdown",
                    metadataJson: null,
                    attachments: _session.CreatedAssistantAttachments,
                    ct: persistCt);

                _chatSuggestions?.Update(_session.Messages);
                yield return _session.Messages;
                yield break;
            }

            var fallbackText = string.IsNullOrWhiteSpace(json) ? "No edit result returned." : json;
            _session.AddMessage(new ChatMessage(ChatRole.Assistant, fallbackText));

            await _repo.AppendMessageAsync(
                conversationId,
                senderRole: "assistant",
                content: fallbackText,
                contentType: "text/markdown",
                metadataJson: null,
                attachments: _session.CreatedAssistantAttachments,
                ct: persistCt);

            _chatSuggestions?.Update(_session.Messages);
            yield return _session.Messages;
            yield break;
        }

        // Search path unchanged
        if (LooksLikeDocumentQuestion(userText))
        {
            _session.ClearStreamingMessage();

            var query = rawUserQuery;
            var json = await _docSearchTool.SearchDocumentsAsync(conversationId, query);

            if (TryParseDocSearchJson(json, out var docPayload) && docPayload is not null)
            {
                var roleIds = await GetRoleIdsAsync(persistCt);

                var listObj = (object)_session.CreatedAssistantAttachments;
                var listType = listObj.GetType();
                var attachmentType = listType.IsGenericType
                    ? listType.GetGenericArguments()[0]
                    : throw new InvalidOperationException("CreatedAssistantAttachments must be List<T>.");

                var created = await MaterializeDocSearchAttachmentsAsync(
                    attachmentType,
                    docPayload.Attachments ?? Array.Empty<DocumentAttachmentDescriptor>(),
                    roleIds,
                    persistCt);

                var asIList = (IList)listObj;
                foreach (var a in created)
                    asIList.Add(a);

                var assistantText2 = _attachments.AppendAttachmentLinks(
                    docPayload.Text ?? string.Empty,
                    _session.CreatedAssistantAttachments);

                _session.AddMessage(new ChatMessage(ChatRole.Assistant, assistantText2));

                await _repo.AppendMessageAsync(
                    conversationId,
                    senderRole: "assistant",
                    content: assistantText2,
                    contentType: "text/markdown",
                    metadataJson: null,
                    attachments: _session.CreatedAssistantAttachments,
                    ct: persistCt);

                _chatSuggestions?.Update(_session.Messages);
                yield return _session.Messages;
                yield break;
            }
            else
            {
                var assistantText2 = json ?? "No relevant information found.";
                _session.AddMessage(new ChatMessage(ChatRole.Assistant, assistantText2));

                await _repo.AppendMessageAsync(
                    conversationId,
                    senderRole: "assistant",
                    content: assistantText2,
                    contentType: "text/markdown",
                    metadataJson: null,
                    attachments: _session.CreatedAssistantAttachments,
                    ct: persistCt);

                _chatSuggestions?.Update(_session.Messages);
                yield return _session.Messages;
                yield break;
            }
        }

        // Orchestrator path unchanged
        await foreach (var update in _orchestratorAgent.RunStreamingAsync(
            messages: ChatMessageWindow.ToSafeTextOnlyMessages(_session.Messages, takeLast: 40),
            cancellationToken: streamCt))
        {
            responseText.Text += update.Text;
            ChatMessageItem.NotifyChanged(inProgress);
            yield return _session.Messages;
        }

        var rawAssistant = GetText(_session.CurrentResponseMessage);
        Console.WriteLine("RAW ASSISTANT OUTPUT:" + rawAssistant);

        var assistantText = _attachments.AppendAttachmentLinks(
            rawAssistant,
            _session.CreatedAssistantAttachments);

        var firstImage = _session.CreatedAssistantAttachments
            .FirstOrDefault(a => a.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

        if (firstImage.AttachmentId != Guid.Empty &&
            !assistantText.Contains(firstImage.StorageUrl, StringComparison.OrdinalIgnoreCase))
        {
            assistantText = $"![chart]({firstImage.StorageUrl})\n\n" + assistantText;
        }

        _session.AddMessage(new ChatMessage(ChatRole.Assistant, assistantText));
        _session.ClearStreamingMessage();

        await _repo.AppendMessageAsync(
            conversationId,
            senderRole: "assistant",
            content: assistantText,
            contentType: "text/markdown",
            metadataJson: null,
            attachments: _session.CreatedAssistantAttachments,
            ct: persistCt);

        _chatSuggestions?.Update(_session.Messages);

        yield return _session.Messages;
    }

    private static string StripMarkdownLinks(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        return System.Text.RegularExpressions.Regex.Replace(
            input,
            @"\[(?<text>[^\]]+)\]\((?<url>[^)]+)\)",
            "${text}",
            System.Text.RegularExpressions.RegexOptions.Multiline);
    }

    private static string GetText(ChatMessage? m)
    {
        if (m is null) return string.Empty;

        return string.Concat(
            m.Contents
             .OfType<TextContent>()
             .Select(t => t.Text));
    }

    private static string? ExtractTitleFrom(ChatMessage userMessage)
    {
        var text = GetText(userMessage)?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        var firstLine = text.Split('\n', '\r').FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(firstLine)) return null;

        return firstLine.Length <= 60 ? firstLine : firstLine[..60];
    }
}