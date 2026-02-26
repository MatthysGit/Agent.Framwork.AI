using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Models;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using System;
using System.ComponentModel;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ai.AgentFramwork.Massar.Web.Tools;

public sealed class DocumentSearchTool
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AuthenticationStateProvider _auth;
    private readonly IUnifiedDocumentSearchService _search;
    private readonly ChatSession _session;
    private readonly IChatClient _llm;
    
    // IMPORTANT: Must match the EmbeddingModel stored in BOTH embedding tables
    public const string DefaultEmbeddingModel = "text-embedding-3-large";

    public DocumentSearchTool(
        IDbContextFactory<AppDbContext> dbFactory,
        AuthenticationStateProvider auth,
        IUnifiedDocumentSearchService search,
        ChatSession session,
        IChatClient llm)
    {
        _dbFactory = dbFactory;
        _auth = auth;
        _search = search;
        _session = session;  
        _llm = llm;
    }

    //[Description("Search both global and scoped documents for the current conversation. Returns JSON with text + attachments.")]
    //public async Task<string> SearchDocumentsAsync(
    //    [Description("ConversationId")] Guid conversationId,
    //    [Description("User query")] string query,
    //    [Description("Max results")] int topK = 5,
    //    CancellationToken ct = default)
    //{
    //    var roleIds = await GetRoleIdsAsync();
    //    if (roleIds.Length == 0)
    //    {
    //        return JsonSerializer.Serialize(new DocumentSearchAgentResponse(
    //            Text: "You are not authorized to search documents.",
    //            Attachments: Array.Empty<DocumentAttachmentDescriptor>()
    //        ));
    //    }

    //    var id = _session.ActiveConversationId!.Value;

    //    var hits = await _search.SearchAsync(query, conversationId, roleIds, DefaultEmbeddingModel, topK, ct);

    //    if (hits.Count == 0)
    //    {
    //        return JsonSerializer.Serialize(new DocumentSearchAgentResponse(
    //            Text: "No relevant documents found.",
    //            Attachments: Array.Empty<DocumentAttachmentDescriptor>()
    //        ));
    //    }

    //    // Create attachment descriptors (de-dupe)
    //    var attachments = hits
    //        .Select(h =>
    //        {
    //            if (h.Store == DocStore.Scoped && h.AttachmentId != null)
    //                return new DocumentAttachmentDescriptor(DocStore.Scoped, h.AttachmentId.Value, h.Title);

    //            if (h.Store == DocStore.Global && h.DocumentFileId != null)
    //                return new DocumentAttachmentDescriptor(DocStore.Global, h.DocumentFileId.Value, h.Title);

    //            return null;
    //        })
    //        .Where(x => x != null)
    //        .GroupBy(x => (x!.Store, x!.Id))
    //        .Select(g => g.First()!)
    //        .Take(3) // don’t spam attachments
    //        .ToArray();

    //    // Simple text response with snippets (agent can rewrite, but this is fine)
    //    var lines = hits.Take(5).Select(h =>
    //        h.Store == DocStore.Global
    //            ? $"- **{h.Title}** (page {h.PageNumber?.ToString() ?? "?"}): {h.Snippet}"
    //            : $"- **{h.Title}**: {h.Snippet}"
    //    );

    //    var text =
    //        "I found relevant information in the following documents:\n" +
    //        string.Join("\n", lines) +
    //        (attachments.Length > 0 ? "\n\nI attached the most relevant document(s)." : "");



    //    var pretty = FormatDocSearchTextAsMarkdown(CleanExtractedText(text));

    //    return JsonSerializer.Serialize(new DocumentSearchAgentResponse(pretty, attachments));
    //}

    [Description("Search both global and scoped documents for the current conversation. Returns JSON with text + attachments.")]
    public async Task<string> SearchDocumentsAsync(
    [Description("ConversationId")] Guid conversationId,
    [Description("User query")] string query,
    [Description("Max results")] int topK = 8,
    CancellationToken ct = default)
    {
        var roleIds = await GetRoleIdsAsync();
        if (roleIds.Length == 0)
        {
            return JsonSerializer.Serialize(new DocumentSearchAgentResponse(
                Text: "You are not authorized to search documents.",
                Attachments: Array.Empty<DocumentAttachmentDescriptor>()
            ));
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return JsonSerializer.Serialize(new DocumentSearchAgentResponse(
                Text: "Please enter a question to search your documents.",
                Attachments: Array.Empty<DocumentAttachmentDescriptor>()
            ));
        }

        var hits = await _search.SearchAsync(query, conversationId, roleIds, DefaultEmbeddingModel, topK, ct);

        if (hits.Count == 0)
        {
            return JsonSerializer.Serialize(new DocumentSearchAgentResponse(
                Text: "No relevant documents found.",
                Attachments: Array.Empty<DocumentAttachmentDescriptor>()
            ));
        }

        // Attachments (de-dupe)
        var attachments = hits
            .Select(h =>
            {
                //Don't return the attachment for Scoped store 
                //if (h.Store == DocStore.Scoped && h.AttachmentId != null)
                //    return new DocumentAttachmentDescriptor(DocStore.Scoped, h.AttachmentId.Value, h.Title);

                if (h.Store == DocStore.Global && h.DocumentFileId != null)
                    return new DocumentAttachmentDescriptor(DocStore.Global, h.DocumentFileId.Value, h.Title);

                return null;
            })
            .Where(x => x != null)
            .GroupBy(x => (x!.Store, x!.Id))
            .Select(g => g.First()!)
            .Take(3)
            .ToArray();

        // Build compact evidence for the LLM (no filenames/pages)
        var evidence = string.Join("\n\n",
            hits.Take(Math.Min(20, hits.Count)).Select((h, i) =>
            {
                var snippet = CleanExtractedText(h.Snippet ?? string.Empty);
                snippet = TrimForPrompt(snippet, 900);

                return $"[Evidence {i + 1}] {snippet}";
            }));

        // Professional rewrite via Microsoft.Extensions.AI
        var pretty = await RewriteToProfessionalMarkdownAsync(query, evidence, ct);

        // Guardrails / fallback
        if (string.IsNullOrWhiteSpace(pretty))
            pretty = "No relevant information found in the documents.";

        return JsonSerializer.Serialize(new DocumentSearchAgentResponse(pretty, attachments));
    }

    private async Task<string> RewriteToProfessionalMarkdownAsync(string question, string evidence, CancellationToken ct)
    {
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
    {
        new(ChatRole.System,
@"You are a professional assistant answering questions using internal document excerpts.

Rules:
- Use ONLY the provided evidence.
- If the evidence does not clearly support an item, do not include it.
- Do NOT mention document names, file names, or page numbers.
- Output must be clean, professional markdown.
- If the user asks 'what is included', return a bullet list of included items, then one short concluding sentence."
        ),
        new(ChatRole.User,
$@"Question:
{question}

Evidence:
{evidence}"
        )
    };

        // The exact API shape varies slightly by package version.
        // This pattern works for Microsoft.Extensions.AI's IChatClient:
        var response = await _llm.GetResponseAsync(messages, cancellationToken: ct);

        // Extract assistant text robustly
        var text = string.Concat(
            response.Messages
                .SelectMany(m => m.Contents)
                .OfType<TextContent>()
                .Select(t => t.Text));

        return text.Trim();
    }

    private static string CleanExtractedText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = raw;

        // Remove TOC dot leaders / repeated dots
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\.{2,}", " ");

        // Fix common OCR merged words
        text = System.Text.RegularExpressions.Regex.Replace(text, @"([a-z])([A-Z])", "$1 $2");

        // Remove copyright noise
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\(c\)[^\r\n]*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Normalize whitespace
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

        return text.Trim();
    }

    private static string TrimForPrompt(string s, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        return s.Length <= maxChars ? s : s[..maxChars] + "…";
    }

   
    

    private static string FormatDocSearchTextAsMarkdown(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return "No relevant documents found.";

        // Split lines and remove the noisy header
        var lines = rawText
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        // Remove default “I found…” header if present
        if (lines.Count > 0 && lines[0].StartsWith("I found relevant information", StringComparison.OrdinalIgnoreCase))
            lines.RemoveAt(0);

        // Remove “I attached…” footer if present
        lines.RemoveAll(l => l.StartsWith("I attached", StringComparison.OrdinalIgnoreCase));

        // Convert "File (page ?): snippet" to just "snippet"
        static string StripFilePrefix(string line)
        {
            // Handles:
            // - **Example.pdf** (page ?): something...
            // - Example.pdf (page ?): something...
            // - **Example.docx**: something...
            var idx = line.IndexOf("):", StringComparison.Ordinal);
            if (idx >= 0) return line[(idx + 2)..].Trim();

            idx = line.IndexOf("**:", StringComparison.Ordinal); // rare
            if (idx >= 0) return line[(idx + 3)..].Trim();

            idx = line.IndexOf("**", StringComparison.Ordinal);
            if (idx >= 0)
            {
                // If it looks like "- **file**: snippet"
                var colon = line.IndexOf(":", StringComparison.Ordinal);
                if (colon >= 0 && colon < 200) return line[(colon + 1)..].Trim();
            }

            // If it looks like "File.ext: snippet"
            var extColon = line.IndexOf(":", StringComparison.Ordinal);
            if (extColon > 0 && extColon < 200 && line[..extColon].Contains('.'))
                return line[(extColon + 1)..].Trim();

            return line;
        }

        var cleaned = lines
            .Select(StripFilePrefix)
            .Select(l => l.TrimStart('-', '•', ' '))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8) // keep it readable
            .ToList();

        if (cleaned.Count == 0)
            return "No relevant documents found.";

        // Build nice markdown
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Here’s what I found in the document:");
        sb.AppendLine();

        foreach (var c in cleaned)
            sb.AppendLine($"- {c}");

        return sb.ToString().Trim();
    }
    

    private async Task<int[]> GetRoleIdsAsync(CancellationToken ct = default)
    {
        var state = await _auth.GetAuthenticationStateAsync();
        var user = state.User;

        // Pull role ids from claims
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
}