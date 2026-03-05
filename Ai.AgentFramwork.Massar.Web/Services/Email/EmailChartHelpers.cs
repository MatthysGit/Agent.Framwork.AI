using Ai.AgentFramwork.Massar.Web.Models;
using Ai.AgentFramwork.Massar.Web.Services.Email;

public static class EmailChartHelpers
{
    /// <summary>
    /// Builds an HTML section with &lt;img src="cid:..."&gt; tags and matching inline attachments.
    /// </summary>
    public static (string Html, IEnumerable<EmailAttachment> Attachments) BuildInlineChartSection(IReadOnlyList<ExcelChartArtifact> charts)
    {
        if (charts is null || charts.Count == 0)
            return (string.Empty, Array.Empty<EmailAttachment>());

        var atts = new List<EmailAttachment>();
        var html = new System.Text.StringBuilder();

        html.AppendLine(@"<div style=""font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:#111;"">");
        html.AppendLine(@"  <div style=""font-weight:600;margin:12px 0 6px 0;"">Charts</div>");

        for (int i = 0; i < charts.Count; i++)
        {
            var ch = charts[i];
            var cid = $"{System.Guid.NewGuid()}@inline";

            byte[] bytes;
            try { bytes = Convert.FromBase64String(ch.Base64 ?? ""); }
            catch { continue; }

            atts.Add(new EmailAttachment(
                FileName: string.IsNullOrWhiteSpace(ch.FileName) ? $"chart_{i + 1}.png" : ch.FileName,
                ContentType: string.IsNullOrWhiteSpace(ch.ContentType) ? "image/png" : ch.ContentType,
                ContentBytes: bytes,
                IsInline: true,
                ContentId: cid));

            var title = System.Net.WebUtility.HtmlEncode(ch.Title ?? $"Chart {i + 1}");
            html.AppendLine($@"  <div style=""margin:0 0 10px 0;"">");
            html.AppendLine($@"    <div style=""font-size:12px;color:#444;margin:0 0 4px 0;"">{title}</div>");
            html.AppendLine($@"    <img src=""cid:{cid}"" alt=""{title}"" style=""max-width:100%;border:1px solid #eee;"" />");
            html.AppendLine(@"  </div>");
        }

        html.AppendLine(@"</div>");
        return (html.ToString(), atts);
    }
}