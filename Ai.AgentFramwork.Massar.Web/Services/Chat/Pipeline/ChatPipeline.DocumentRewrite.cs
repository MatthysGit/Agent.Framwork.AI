using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;
namespace Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;

public sealed partial class ChatPipeline
{
    private async Task<PipelineResult> ExecuteDocumentRewriteAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        var conversationId = _getConversationId();
        var sourceBlob = await ResolveRewriteSourceBlobAsync(conversationId, userText, ct);

        if (sourceBlob is null || sourceBlob.FileContent is null || sourceBlob.FileContent.Length == 0)
        {
            return new PipelineResult(
                JsonSerializer.Serialize(new DocumentSearchAgentResponse(
                    "I could not find an uploaded document in this conversation to rewrite. Please attach a file and try again.",
                    Array.Empty<DocumentAttachmentDescriptor>())),
                ChatAgentFactory.DocumentRewriteAgentName,
                routerReason);
        }

        var sourceText = await _documentTextExtractor.ExtractAsync(
            sourceBlob.FileContent,
            sourceBlob.ContentType ?? string.Empty,
            sourceBlob.FileName ?? "document.docx");

        sourceText = NormalizeForRewrite(sourceText);
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return new PipelineResult(
                JsonSerializer.Serialize(new DocumentSearchAgentResponse(
                    $"I found '{sourceBlob.FileName}', but I could not extract readable text from it.",
                    Array.Empty<DocumentAttachmentDescriptor>())),
                ChatAgentFactory.DocumentRewriteAgentName,
                routerReason);
        }

        var style = DetectRewriteStyle(userText);
        var rewritePrompt = BuildRewritePrompt(userText, style, sourceBlob.FileName ?? "document", sourceText);

        await TrackRouteAsync(ChatAgentFactory.DocumentRewriteAgentName, "synthesis", ct);
        var rewriteCall = await _caller.CallAgentAsync(
            ChatAgentFactory.DocumentRewriteAgentName,
            rewritePrompt,
            cancellationToken: ct);

        var rewrittenText = (rewriteCall.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rewrittenText))
        {
            rewrittenText = "I could not generate a rewritten version of the uploaded document.";
        }

        rewrittenText = await RunPpiSafeAsync(userText, rewrittenText, ct);

        var fileName = BuildRewrittenFileName(sourceBlob.FileName ?? "document.docx", style);
        var docxBytes = BuildRewrittenDocx(fileName, rewrittenText);
        var saved = await _attachmentStore.SaveAssistantFileAsync(
            conversationId,
            fileName,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            docxBytes,
            ct);

        var responseJson = JsonSerializer.Serialize(new DocumentSearchAgentResponse(
            rewrittenText,
            new[]
            {
                new DocumentAttachmentDescriptor(DocStore.Scoped, saved.AttachmentId, saved.FileName)
            }));

        return new PipelineResult(
            responseJson,
            ChatAgentFactory.DocumentRewriteAgentName,
            routerReason + " (rewritten from uploaded attachment and returned as downloadable file)");
    }

    private async Task<ChatAttachmentBlob?> ResolveRewriteSourceBlobAsync(Guid conversationId, string userText, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var attachments = await (
            from a in db.ChatMessageAttachments.AsNoTracking()
            join m in db.ChatMessages.AsNoTracking() on a.MessageId equals m.MessageId
            join b in db.ChatAttachmentBlobs.AsNoTracking() on a.AttachmentId equals b.AttachmentId
            where m.ConversationId == conversationId && !a.IsFromAgent
            orderby a.CreatedUtc descending
            select new { Attachment = a, Blob = b }
        ).ToListAsync(ct);

        if (attachments.Count == 0)
            return null;

        var requestedName = ExtractRequestedFileName(userText);
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            var exact = attachments.FirstOrDefault(x => string.Equals(x.Attachment.FileName, requestedName, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact.Blob;
        }

        var hinted = attachments.FirstOrDefault(x => userText.Contains(x.Attachment.FileName, StringComparison.OrdinalIgnoreCase));
        if (hinted is not null)
            return hinted.Blob;

        return attachments[0].Blob;
    }

    private static string? ExtractRequestedFileName(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return null;

        var match = Regex.Match(userText, @"(?<name>[\w\-. ]+\.(docx|pdf|txt))", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["name"].Value.Trim() : null;
    }

    private static string DetectRewriteStyle(string userText)
    {
        var t = userText?.ToLowerInvariant() ?? string.Empty;
        if (t.Contains("board")) return "board-ready";
        if (t.Contains("customer")) return "customer-friendly";
        if (t.Contains("concise")) return "concise";
        if (t.Contains("formal")) return "formal-business";
        if (t.Contains("executive")) return "executive";
        return "formal-business";
    }

    private static string BuildRewritePrompt(string userText, string style, string fileName, string sourceText)
        => $"""
You are rewriting a user-uploaded business document.

Task:
- Rewrite the FULL document content in the requested style.
- Do not summarize.
- Do not omit major sections unless the user explicitly asked for a concise rewrite.
- Preserve facts, numbers, names, decisions, risks, and actions.
- Preserve the original document meaning.
- Keep headings and section flow when possible.
- Return only the rewritten document text, ready to place into a downloadable file.

Requested style: {style}
User request: {userText}
Source file: {fileName}

Document text:
{sourceText}
""";

    private static string NormalizeForRewrite(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        text = text.Replace("\r", string.Empty);
        text = Regex.Replace(text, "\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string BuildRewrittenFileName(string originalFileName, string style)
    {
        var baseName = Path.GetFileNameWithoutExtension(originalFileName);
        var safeStyle = Regex.Replace(style, "[^a-z0-9]+", "-", RegexOptions.IgnoreCase).Trim('-').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(safeStyle)) safeStyle = "rewrite";
        return $"{baseName}-{safeStyle}.docx";
    }

    private static byte[] BuildRewrittenDocx(string title, string rewrittenText)
    {
        using var stream = new MemoryStream();
        using (var wordDoc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = wordDoc.AddMainDocumentPart();
            mainPart.Document = new WordDocument(new Body());
            var body = mainPart.Document.Body!;

            body.AppendChild(new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Title" }),
                new Run(new Text(title) { Space = SpaceProcessingModeValues.Preserve })));

            foreach (var block in SplitIntoParagraphs(rewrittenText))
            {
                body.AppendChild(new Paragraph(
                    new Run(new Text(block) { Space = SpaceProcessingModeValues.Preserve })));
            }

            mainPart.Document.Save();
        }

        return stream.ToArray();
    }

    private static IEnumerable<string> SplitIntoParagraphs(string text)
    {
        using var reader = new StringReader(text ?? string.Empty);
        var sb = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString().Trim();
                    sb.Clear();
                }
                continue;
            }

            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(line.Trim());
        }

        if (sb.Length > 0)
            yield return sb.ToString().Trim();
    }
}