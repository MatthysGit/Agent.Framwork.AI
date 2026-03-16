using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ai.AgentFramwork.Massar.Web.Models;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

/// <summary>
/// Turns base64 chart artifacts (produced by ExcelAnalyticsTool) into real chat attachments,
/// then returns a markdown section with working image links for the chat UI.
///
/// Why: markdown like ![Sales Trend]() renders as a broken image icon. Outlook can’t fetch
/// app-authenticated URLs either, so we store the images in dbo.ChatAttachmentBlob and
/// reference them via /api/chat/attachments/{id}.
/// </summary>
public sealed class ExcelVisualAidsBuilder
{
    private readonly ChatAttachmentStore _attachments;

    public ExcelVisualAidsBuilder(ChatAttachmentStore attachments)
        => _attachments = attachments;

    /// <summary>
    /// Saves each chart image as a chat attachment (searchable in the same conversation)
    /// and returns markdown that will render images in the chat window.
    /// </summary>
    public async Task<string> BuildMarkdownAsync(
        Guid conversationId,
        IReadOnlyList<ExcelChartArtifact>? charts,
        CancellationToken ct = default)
    {
        if (charts is null || charts.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("**Visual Aids (Extracted from File):**");

        var i = 1;
        foreach (var c in charts)
        {
            ct.ThrowIfCancellationRequested();

            if (c is null) continue;
            if (string.IsNullOrWhiteSpace(c.Base64)) continue;

            byte[] bytes;
            try { bytes = Convert.FromBase64String(c.Base64); }
            catch { continue; }

            if (bytes.Length == 0) continue;

            // Persist as an assistant attachment for THIS conversation so it can be fetched
            // by the UI and (optionally) included in ImproveConversation email later.
            var safeTitle = string.IsNullOrWhiteSpace(c.Title) ? $"Chart {i}" : c.Title.Trim();
            var fileName = string.IsNullOrWhiteSpace(c.FileName) ? $"excel-chart-{i}.png" : c.FileName;

            var info = await _attachments.SaveAssistantFileAsync(
                conversationId,
                fileName,
                contentType: string.IsNullOrWhiteSpace(c.ContentType) ? "image/png" : c.ContentType,
                bytes: bytes,
                ct: ct);

            // Build a stable absolute/relative URL for the app.
            // The chat UI will request it with the user’s cookies, so auth works.
            var url = info.StorageUrl;

            sb.AppendLine();
            sb.AppendLine($"- {safeTitle}");
            sb.AppendLine($"  ");
            sb.AppendLine($"  ![{EscapeAlt(safeTitle)}]({url})");

            i++;
        }

        // If nothing could be saved (bad base64), return empty.
        var result = sb.ToString().Trim();
        if (result.EndsWith("**Visual Aids (Extracted from File):**", StringComparison.Ordinal))
            return string.Empty;

        return result + "\n";
    }

    private static string EscapeAlt(string s)
        => s.Replace("\r", " ").Replace("\n", " ").Trim();
}

