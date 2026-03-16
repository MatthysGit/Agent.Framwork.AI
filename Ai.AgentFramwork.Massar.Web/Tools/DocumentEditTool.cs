using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using OpenAI.Chat;
using System.ComponentModel;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ai.AgentFramwork.Massar.Web.Tools;

#pragma warning disable OPENAI001
public sealed class DocumentEditTool
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AuthenticationStateProvider _auth;
    private readonly IUnifiedDocumentSearchService _search;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IAgentModelSelector _modelSelector;
    private readonly IAiUsageLogger? _aiUsageLogger;
    private readonly IAiUsageContextAccessor? _aiUsageContextAccessor;

    public const string DefaultEmbeddingModel = "text-embedding-3-large";
    private const int MaxCommentsPerDocument = 60;
    private const int MaxCommentsPerChunk = 12;
    private const int ChunkParagraphCount = 18;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public DocumentEditTool(
        IDbContextFactory<AppDbContext> dbFactory,
        AuthenticationStateProvider auth,
        IUnifiedDocumentSearchService search,
        IChatClientFactory chatClientFactory,
        IAgentModelSelector modelSelector,
        IAiUsageLogger? aiUsageLogger = null,
        IAiUsageContextAccessor? aiUsageContextAccessor = null)
    {
        _dbFactory = dbFactory;
        _auth = auth;
        _search = search;
        _chatClientFactory = chatClientFactory;
        _modelSelector = modelSelector;
        _aiUsageLogger = aiUsageLogger;
        _aiUsageContextAccessor = aiUsageContextAccessor;
    }

    [Description("Finds a matching Word document in the current conversation, reviews the full document, adds multiple Word comments, and returns the reviewed file as an attachment.")]
    public Task<string> EditDocumentAsync(
        [Description("The current conversation id.")] Guid conversationId,
        [Description("The document search phrase or filename hint.")] string query,
        [Description("The review or editing instruction. Broad instructions trigger a full review.")] string editInstruction,
        [Description("How many top candidate documents to search. Usually keep as 1.")] int topK = 1,
        CancellationToken ct = default)
        => ReviewDocumentAsync(conversationId, query, editInstruction, topK, ct);

    public async Task<string> ReviewDocumentAsync(
        Guid conversationId,
        string query,
        string editInstruction,
        int topK = 1,
        CancellationToken ct = default)
    {
        if (conversationId == Guid.Empty)
            return SerializeResponse("Conversation id is missing, so I could not review the document.", Array.Empty<object>());

        if (string.IsNullOrWhiteSpace(query))
            return SerializeResponse("Please enter a document to review.", Array.Empty<object>());

        var hits = await _search.SearchScopedAsync(query, conversationId, DefaultEmbeddingModel, Math.Max(1, topK), ct);
        if (hits.Count == 0)
            return SerializeResponse("No relevant documents found for review.", Array.Empty<object>());

        var scopedHit = hits.FirstOrDefault(h => h.Store == DocStore.Scoped && h.AttachmentId != null);
        if (scopedHit == null)
            return SerializeResponse("No scoped document found for review.", Array.Empty<object>());

        var roleIds = await GetRoleIdsAsync(ct);
        _ = roleIds; // reserved if you later want role-aware review rules

        ChatAttachmentBlob? sourceBlob;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            sourceBlob = await ResolveSourceBlobAsync(db, conversationId, scopedHit.AttachmentId, ct);
        }

        if (sourceBlob?.FileContent == null || sourceBlob.FileContent.Length == 0)
            return SerializeResponse("Attachment not found or has no content.", Array.Empty<object>());

        var fileName = string.IsNullOrWhiteSpace(sourceBlob.FileName)
            ? (string.IsNullOrWhiteSpace(scopedHit.Title) ? "reviewed-document.docx" : scopedHit.Title)
            : sourceBlob.FileName;

        if (!fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            return SerializeResponse("I found the file, but full review comments are currently supported only for .docx Word documents.", Array.Empty<object>());

        var paragraphs = ExtractParagraphs(sourceBlob.FileContent);
        if (paragraphs.Count == 0)
            return SerializeResponse("The document appears empty or I could not extract readable paragraphs for review.", Array.Empty<object>());

        var comments = await GenerateReviewCommentsAsync(paragraphs, editInstruction, ct);
        comments = NormalizeAndDedupeComments(comments, paragraphs)
            .Take(MaxCommentsPerDocument)
            .ToList();

        if (comments.Count == 0)
            return SerializeResponse("I reviewed the document and did not find any actionable comments to add.", Array.Empty<object>());

        var reviewedBytes = AddCommentsToWordDocumentInMemory(sourceBlob.FileContent, comments);
        var reviewedAttachmentId = Guid.NewGuid();
        var reviewedFileName = BuildReviewedFileName(fileName);

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            db.ChatAttachmentBlobs.Add(new ChatAttachmentBlob
            {
                AttachmentId = reviewedAttachmentId,
                FileName = reviewedFileName,
                ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                FileSizeBytes = reviewedBytes.Length,
                FileContent = reviewedBytes,
                CreatedUtc = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);
        }

        var attachments = new[]
        {
            new
            {
                Store = DocStore.Scoped,
                Id = reviewedAttachmentId.ToString(),
                FileName = reviewedFileName
            }
        };

        var summary = BuildSummaryText(comments);
        return SerializeResponse(summary, attachments);
    }

    private async Task<ChatAttachmentBlob?> ResolveSourceBlobAsync(
        AppDbContext db,
        Guid conversationId,
        Guid? attachmentId,
        CancellationToken ct)
    {
        if (attachmentId.HasValue)
        {
            var exactBlob = await db.ChatAttachmentBlobs
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.AttachmentId == attachmentId.Value, ct);

            if (exactBlob != null)
                return exactBlob;
        }

        return await (
            from convo in db.ChatConversations.AsNoTracking()
            join msg in db.ChatMessages.AsNoTracking() on convo.ConversationId equals msg.ConversationId
            join att in db.ChatMessageAttachments.AsNoTracking() on msg.MessageId equals att.MessageId
            join blob in db.ChatAttachmentBlobs.AsNoTracking() on att.AttachmentId equals blob.AttachmentId
            where convo.ConversationId == conversationId
            orderby blob.CreatedUtc descending
            select blob
        ).FirstOrDefaultAsync(ct);
    }

    private List<DocumentParagraph> ExtractParagraphs(byte[] docBytes)
    {
        using var stream = new MemoryStream(docBytes, writable: false);
        using var wordDoc = WordprocessingDocument.Open(stream, false);

        var body = wordDoc.MainDocumentPart?.Document?.Body;
        if (body == null)
            return new List<DocumentParagraph>();

        var paragraphs = body.Descendants<Paragraph>()
            .Select((p, i) => new DocumentParagraph(i, NormalizeWhitespace(p.InnerText)))
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .ToList();

        return paragraphs;
    }

    private async Task<List<ReviewComment>> GenerateReviewCommentsAsync(
        IReadOnlyList<DocumentParagraph> paragraphs,
        string editInstruction,
        CancellationToken ct)
    {
        var modelKey = _modelSelector.GetModelForAgent(ChatAgentFactory.DocumentEditAgentName);
        ChatClient chatClient = _chatClientFactory.Create(modelKey);

        var results = new List<ReviewComment>();

        foreach (var chunk in Chunk(paragraphs, ChunkParagraphCount))
        {
            var prompt = BuildReviewPrompt(chunk, editInstruction);
            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new SystemChatMessage(BuildSystemPrompt()),
                new UserChatMessage(prompt)
            };

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await chatClient.CompleteChatAsync(messages, cancellationToken: ct);
                sw.Stop();
                var text = response.Value?.Content?.FirstOrDefault()?.Text?.Trim() ?? "[]";

                if (_aiUsageLogger is not null)
                {
                    var ctx = _aiUsageContextAccessor?.GetCurrent();
                    var usageModelKey = _modelSelector.GetModelForAgent(ChatAgentFactory.DocumentEditAgentName);
                    _aiUsageContextAccessor?.SetAgent(ChatAgentFactory.DocumentEditAgentName, usageModelKey);
                    var snapshot = AiUsageReflection.ExtractSnapshot(response);
                    var promptText = string.Concat(messages.OfType<OpenAI.Chat.ChatMessage>().Select(m => m.Content.FirstOrDefault()?.Text ?? string.Empty));
                    var estimatedPrompt = snapshot.PromptTokens ?? AiTokenEstimator.EstimateTextTokens(promptText);
                    var estimatedCompletion = snapshot.CompletionTokens ?? AiTokenEstimator.EstimateTextTokens(text);

                    await _aiUsageLogger.LogAsync(new AiUsageLogRequest(
                        AgentName: ChatAgentFactory.DocumentEditAgentName,
                        ModelName: usageModelKey,
                        UserId: ctx?.UserId,
                        ConversationId: ctx?.ConversationId,
                        MessageId: ctx?.MessageId,
                        PromptTokens: snapshot.PromptTokens ?? estimatedPrompt,
                        CompletionTokens: snapshot.CompletionTokens ?? estimatedCompletion,
                        TotalTokens: snapshot.TotalTokens ?? (estimatedPrompt + estimatedCompletion),
                        CachedInputTokens: snapshot.CachedInputTokens,
                        ReasoningTokens: snapshot.ReasoningTokens,
                        RequestId: snapshot.RequestId,
                        ClientRequestId: ctx?.ClientRequestId,
                        LatencyMs: (int)sw.ElapsedMilliseconds,
                        Succeeded: true,
                        ErrorMessage: null), ct);
                }

                var parsed = ParseReviewComments(text);
                if (parsed.Count > 0)
                    results.AddRange(parsed);
            }
            catch
            {
                // Ignore chunk failures and continue with the rest so review still completes.
            }
        }

        return results;
    }

    private static string BuildSystemPrompt() =>
        """
You are a meticulous business document reviewer.
Review the supplied paragraphs and return ONLY a JSON array.
Each array item must follow this shape:
{
  "paragraphIndex": 12,
  "severity": "low|medium|high",
  "category": "clarity|grammar|tone|structure|consistency|ambiguity|formatting|completeness|repetition|other",
  "comment": "specific actionable comment",
  "suggestedRewrite": "optional improved wording or empty string"
}
Rules:
- Return only actionable comments.
- Do not comment on every paragraph.
- Skip trivial edits unless they materially improve readability or professionalism.
- Keep comments concise and useful.
- Use the paragraphIndex values exactly as provided.
- Prefer at most 12 comments for the supplied chunk.
- If there are no worthwhile comments, return []
""";

    private static string BuildReviewPrompt(IReadOnlyList<DocumentParagraph> chunk, string editInstruction)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Review the following document chunk.");
        sb.AppendLine($"User instruction: {NormalizeInstruction(editInstruction)}");
        sb.AppendLine("Evaluate grammar, clarity, tone, consistency, structure, ambiguity, repetition, and completeness as relevant.");
        sb.AppendLine("Return only JSON.");
        sb.AppendLine();

        foreach (var p in chunk)
            sb.AppendLine($"[{p.Index}] {p.Text}");

        return sb.ToString();
    }

    private static List<ReviewComment> ParseReviewComments(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<ReviewComment>();

        text = ExtractJsonArray(text);

        try
        {
            var parsed = JsonSerializer.Deserialize<List<ReviewComment>>(text, JsonOptions);
            return parsed ?? new List<ReviewComment>();
        }
        catch
        {
            return new List<ReviewComment>();
        }
    }

    private static string ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start >= 0 && end >= start)
            return text[start..(end + 1)];
        return "[]";
    }

    private static List<ReviewComment> NormalizeAndDedupeComments(
        IEnumerable<ReviewComment> comments,
        IReadOnlyList<DocumentParagraph> paragraphs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ReviewComment>();

        foreach (var raw in comments ?? Enumerable.Empty<ReviewComment>())
        {
            if (raw == null)
                continue;

            var idx = raw.ParagraphIndex;
            if (idx < 0 || idx >= paragraphs.Count)
                continue;

            var comment = NormalizeWhitespace(raw.Comment);
            if (string.IsNullOrWhiteSpace(comment) || comment.Length < 8)
                continue;

            var rewrite = NormalizeRewrite(raw.SuggestedRewrite);
            var severity = NormalizeSeverity(raw.Severity);
            var category = NormalizeCategory(raw.Category);
            var key = $"{idx}|{comment}";
            if (!seen.Add(key))
                continue;

            results.Add(new ReviewComment
            {
                ParagraphIndex = idx,
                Severity = severity,
                Category = category,
                Comment = comment,
                SuggestedRewrite = rewrite
            });
        }

        return results
            .OrderBy(c => c.ParagraphIndex)
            .ThenByDescending(c => SeverityRank(c.Severity))
            .ThenBy(c => c.Category)
            .Take(MaxCommentsPerDocument)
            .ToList();
    }

    private static byte[] AddCommentsToWordDocumentInMemory(byte[] docBytes, IReadOnlyList<ReviewComment> comments)
    {
        using var memStream = new MemoryStream();
        memStream.Write(docBytes, 0, docBytes.Length);
        memStream.Position = 0;

        using (var wordDoc = WordprocessingDocument.Open(memStream, true))
        {
            var mainPart = wordDoc.MainDocumentPart;
            var body = mainPart?.Document?.Body;
            if (mainPart == null || body == null)
                return docBytes;

            var commentsPart = mainPart.GetPartsOfType<WordprocessingCommentsPart>().FirstOrDefault()
                ?? mainPart.AddNewPart<WordprocessingCommentsPart>();
            commentsPart.Comments ??= new Comments();

            var docParagraphs = body.Descendants<Paragraph>().ToList();
            var nextId = commentsPart.Comments.Elements<Comment>()
                .Select(c => int.TryParse(c.Id?.Value, out var id) ? id : 0)
                .DefaultIfEmpty(0)
                .Max() + 1;

            foreach (var item in comments)
            {
                if (item.ParagraphIndex < 0 || item.ParagraphIndex >= docParagraphs.Count)
                    continue;

                var paragraph = docParagraphs[item.ParagraphIndex];
                var commentId = nextId.ToString();
                nextId++;

                var text = BuildCommentBody(item);
                var comment = new Comment
                {
                    Id = commentId,
                    Author = "Massar Agent",
                    Initials = "MA",
                    Date = DateTime.Now
                };
                comment.AppendChild(new Paragraph(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
                commentsPart.Comments.AppendChild(comment);

                InsertCommentReference(paragraph, commentId);
            }

            commentsPart.Comments.Save();
            mainPart.Document.Save();
        }

        return memStream.ToArray();
    }

    private static void InsertCommentReference(Paragraph paragraph, string commentId)
    {
        var firstRun = paragraph.Elements<Run>().FirstOrDefault();
        var lastRun = paragraph.Elements<Run>().LastOrDefault();

        var start = new CommentRangeStart { Id = commentId };
        var end = new CommentRangeEnd { Id = commentId };
        var reference = new Run(new CommentReference { Id = commentId });

        if (firstRun != null)
            paragraph.InsertBefore(start, firstRun);
        else
            paragraph.PrependChild(start);

        if (lastRun != null)
            paragraph.InsertAfter(end, lastRun);
        else
            paragraph.AppendChild(end);

        paragraph.AppendChild(reference);
    }

    private static string BuildCommentBody(ReviewComment item)
    {
        var sb = new StringBuilder();
        sb.Append('[')
          .Append(item.Category)
          .Append(" | ")
          .Append(item.Severity)
          .AppendLine("]")
          .Append(item.Comment);

        if (!string.IsNullOrWhiteSpace(item.SuggestedRewrite))
        {
            sb.AppendLine()
              .Append("Suggested rewrite: ")
              .Append(item.SuggestedRewrite);
        }

        return sb.ToString();
    }

    private static string BuildReviewedFileName(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext))
            ext = ".docx";
        return $"{baseName}-reviewed{ext}";
    }

    private static string BuildSummaryText(IReadOnlyList<ReviewComment> comments)
    {
        var groups = comments
            .GroupBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();

        var categorySummary = string.Join(", ", groups.Take(4).Select(g => $"{g.Key} ({g.Count()})"));
        return $"Full review applied successfully. Added {comments.Count} comment(s) across the document. Main review areas: {categorySummary}. I attached the reviewed document.";
    }

    private static string SerializeResponse(string text, object attachments)
        => JsonSerializer.Serialize(new { Text = text, Attachments = attachments }, JsonOptions);

    private async Task<int[]> GetRoleIdsAsync(CancellationToken ct = default)
    {
        var state = await _auth.GetAuthenticationStateAsync();
        var user = state.User;

        var roleIds = user.Claims
            .Where(c => c.Type is "roleId" or "RoleId" or ClaimTypes.Role)
            .Select(c => c.Value)
            .Select(v => int.TryParse(v, out var i) ? (int?)i : null)
            .Where(i => i.HasValue)
            .Select(i => i!.Value)
            .Distinct()
            .ToArray();

        return roleIds;
    }

    private static IEnumerable<IReadOnlyList<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.Skip(i).Take(size).ToList();
    }

    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        return Regex.Replace(text.Trim(), @"\s+", " ");
    }

    private static string NormalizeInstruction(string? instruction)
    {
        var cleaned = NormalizeWhitespace(instruction);
        return string.IsNullOrWhiteSpace(cleaned)
            ? "Perform a thorough full-document review and add actionable comments."
            : cleaned;
    }

    private static string? NormalizeRewrite(string? text)
    {
        var cleaned = NormalizeWhitespace(text);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static string NormalizeSeverity(string? severity)
    {
        var s = NormalizeWhitespace(severity).ToLowerInvariant();
        return s switch
        {
            "high" => "high",
            "medium" => "medium",
            "low" => "low",
            _ => "medium"
        };
    }

    private static string NormalizeCategory(string? category)
    {
        var c = NormalizeWhitespace(category).ToLowerInvariant();
        return c switch
        {
            "clarity" => "clarity",
            "grammar" => "grammar",
            "tone" => "tone",
            "structure" => "structure",
            "consistency" => "consistency",
            "ambiguity" => "ambiguity",
            "formatting" => "formatting",
            "completeness" => "completeness",
            "repetition" => "repetition",
            _ => "other"
        };
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0
    };

    private sealed record DocumentParagraph(int Index, string Text);

    private sealed class ReviewComment
    {
        public int ParagraphIndex { get; set; }
        public string Severity { get; set; } = "medium";
        public string Category { get; set; } = "other";
        public string Comment { get; set; } = string.Empty;
        public string? SuggestedRewrite { get; set; }
    }
}
