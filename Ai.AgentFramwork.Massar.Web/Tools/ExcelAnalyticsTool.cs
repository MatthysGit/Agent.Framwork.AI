// File: Tools/ExcelAnalyticsTool.cs
using Ai.AgentFramwork.Massar.Web.Models;
using Ai.AgentFramwork.Massar.Web.Services.Chat.charts;
using Ai.AgentFramwork.Massar.Web.Services.ExcelServices;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ai.AgentFramwork.Massar.Web.Tools;

#pragma warning disable OPENAI001
public sealed class ExcelAnalyticsTool
{
    private readonly IUploadedExcelReader _reader;
    private readonly LineChartRenderer _line;
    private readonly BarChartRenderer _bar;
    private readonly IChatClient _llm;
    private readonly Ai.AgentFramwork.Massar.Web.Services.Chat.ChatAttachmentStore _attachmentStore;
    private readonly Ai.AgentFramwork.Massar.Web.Services.Chat.ChatSession _session;

    public ExcelAnalyticsTool(
        IUploadedExcelReader reader,
        LineChartRenderer line,
        BarChartRenderer bar,
        IChatClient llm,
        Ai.AgentFramwork.Massar.Web.Services.Chat.ChatAttachmentStore attachmentStore,
        Ai.AgentFramwork.Massar.Web.Services.Chat.ChatSession session)
    {
        _reader = reader;
        _line = line;
        _bar = bar;
        _llm = llm;
        _attachmentStore = attachmentStore;
        _session = session;
    }

    [Description("Analyze an uploaded Excel/CSV attachment by attachmentId. Returns JSON with markdown text + email-safe HTML + optional chart artifacts.")]
    public async Task<string> AnalyzeUploadedExcelAsync(
        [Description("AttachmentId (GUID) of the uploaded Excel/CSV file")] Guid attachmentId,
        [Description("User question / analysis request")] string question,
        CancellationToken ct = default)
    {
        if (attachmentId == Guid.Empty)
            return JsonSerializer.Serialize(new ExcelAnalysisAgentResponse(
                Text: "Missing attachmentId.",
                Html: BuildEmptyHtml("Missing attachmentId."),
                Charts: Array.Empty<ExcelChartArtifact>()));

        question ??= string.Empty;

        TabularData table;
        try
        {
            table = await _reader.ReadAsTableAsync(attachmentId, ct);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new ExcelAnalysisAgentResponse(
                Text: $"Failed to read spreadsheet: {ex.Message}",
                Html: BuildEmptyHtml("Failed to read spreadsheet."),
                Charts: Array.Empty<ExcelChartArtifact>()));
        }

        if (table.Columns.Count == 0)
        {
            return JsonSerializer.Serialize(new ExcelAnalysisAgentResponse(
                Text: "No columns found in the spreadsheet.",
                Html: BuildEmptyHtml("No columns found."),
                Charts: Array.Empty<ExcelChartArtifact>()));
        }

        var rows = table.Rows ?? new List<IReadOnlyList<object?>>();
        var profile = ProfileColumns(table, rows, sampleRows: 200);

        var charts = BuildDefaultCharts(table, rows, profile);

        // Evidence for LLM: compact profile + sample rows (no PII)
        var evidence = BuildEvidence(table, rows, profile);

        var markdown = await RewriteToProfessionalMarkdownAsync(question, evidence, ct);
        if (string.IsNullOrWhiteSpace(markdown))
            markdown = BuildFallbackMarkdown(table, rows, profile);

        // ✅ Visual aids in chat: persist chart PNGs as assistant attachments and embed as markdown images.
        // This makes charts render in the chat UI (instead of broken img icons).
        var visualAidsAppendix = await BuildVisualAidsMarkdownAsync(charts, Safe(table.Name), ct);
        if (!string.IsNullOrWhiteSpace(visualAidsAppendix) &&
            !markdown.Contains("### Visual Aids", StringComparison.OrdinalIgnoreCase))
        {
            markdown = markdown.TrimEnd() + "\n\n" + visualAidsAppendix.Trim() + "\n";
        }

        var html = BuildDenseHtmlSummary(table, rows, profile, charts);

        var result = new ExcelAnalysisAgentResponse(
            Text: markdown.Trim(),
            Html: html,
            Charts: charts.ToArray());

        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    // ----------------------------
    // Column profiling
    // ----------------------------
    internal sealed record ColumnProfile(string Name, ColumnKind Kind, int NonNull, int Nulls);

    internal enum ColumnKind
    {
        Number,
        Date,
        Text,
        Mixed
    }

    private static List<ColumnProfile> ProfileColumns(TabularData table, IReadOnlyList<IReadOnlyList<object?>> rows, int sampleRows)
    {
        var cols = table.Columns;
        var counts = new (int nonNull, int nulls, int num, int date, int text)[cols.Count];

        var take = Math.Min(sampleRows, rows.Count);
        for (int r = 0; r < take; r++)
        {
            var row = rows[r];
            for (int c = 0; c < cols.Count; c++)
            {
                object? v = c < row.Count ? row[c] : null;

                if (v is null || (v is string s && string.IsNullOrWhiteSpace(s)))
                {
                    counts[c].nulls++;
                    continue;
                }

                counts[c].nonNull++;

                if (TryToDouble(v, out _)) counts[c].num++;
                else if (TryToDate(v, out _)) counts[c].date++;
                else counts[c].text++;
            }
        }

        var prof = new List<ColumnProfile>(cols.Count);
        for (int c = 0; c < cols.Count; c++)
        {
            var (nonNull, nulls, num, date, text) = counts[c];

            ColumnKind kind;
            var kinds = 0;
            if (num > 0) kinds++;
            if (date > 0) kinds++;
            if (text > 0) kinds++;

            if (kinds == 0) kind = ColumnKind.Text;
            else if (kinds > 1) kind = ColumnKind.Mixed;
            else if (num > 0) kind = ColumnKind.Number;
            else if (date > 0) kind = ColumnKind.Date;
            else kind = ColumnKind.Text;

            prof.Add(new ColumnProfile(cols[c], kind, nonNull, nulls));
        }

        return prof;
    }

    // ----------------------------
    // Chart generation (simple heuristics)
    // ----------------------------
    private List<ExcelChartArtifact> BuildDefaultCharts(TabularData table, IReadOnlyList<IReadOnlyList<object?>> rows, List<ColumnProfile> profile)
    {
        var charts = new List<ExcelChartArtifact>();

        // Identify columns
        int dateCol = FirstIndex(profile, p => p.Kind == ColumnKind.Date);
        int numCol = FirstIndex(profile, p => p.Kind == ColumnKind.Number);
        int textCol = FirstIndex(profile, p => p.Kind == ColumnKind.Text);

        // 1) Trend chart: Date + Number
        if (dateCol >= 0 && numCol >= 0)
        {
            var pairs = new List<(DateTime dt, double val)>();

            foreach (var row in rows)
            {
                if (dateCol >= row.Count || numCol >= row.Count) continue;

                if (!TryToDate(row[dateCol], out var dt)) continue;
                if (!TryToDouble(row[numCol], out var dv)) continue;

                pairs.Add((dt.Date, dv));
            }

            if (pairs.Count >= 2)
            {
                var series = pairs
                    .GroupBy(p => p.dt)
                    .Select(g => (dt: g.Key, val: g.Sum(x => x.val)))
                    .OrderBy(x => x.dt)
                    .Take(40)
                    .ToList();

                var x = series.Select(s => s.dt.ToString("yyyy-MM-dd")).ToList();
                var y = series.Select(s => s.val).ToList();

                var png = _line.RenderPng(
                    title: $"{Safe(table.Name)} • Trend",
                    xAxisLabel: profile[dateCol].Name,
                    yAxisLabel: profile[numCol].Name,
                    xLabels: x,
                    yValues: y);

                charts.Add(new ExcelChartArtifact(
                    Title: "Sales Trend",
                    FileName: "excel_trend.png",
                    ContentType: "image/png",
                    Base64: Convert.ToBase64String(png)));
            }
        }

        // 2) Top categories: Text + Number
        if (textCol >= 0 && numCol >= 0)
        {
            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (textCol >= row.Count || numCol >= row.Count) continue;

                var key = (row[textCol]?.ToString() ?? "").Trim();
                if (string.IsNullOrWhiteSpace(key)) continue;

                if (!TryToDouble(row[numCol], out var dv)) continue;

                map[key] = map.TryGetValue(key, out var cur) ? (cur + dv) : dv;
            }

            var top = map
                .OrderByDescending(kv => kv.Value)
                .Take(12)
                .ToList();

            if (top.Count >= 2)
            {
                var labels = top.Select(t => t.Key).ToList();
                var values = top.Select(t => t.Value).ToList();

                var png = _bar.RenderPng(
                    title: $"{Safe(table.Name)} • Top {profile[textCol].Name}",
                    xAxisLabel: profile[textCol].Name,
                    yAxisLabel: profile[numCol].Name,
                    labels: labels,
                    values: values);

                charts.Add(new ExcelChartArtifact(
                    Title: "Top Categories",
                    FileName: "excel_top_categories.png",
                    ContentType: "image/png",
                    Base64: Convert.ToBase64String(png)));
            }
        }

        return charts;
    }

    private static int FirstIndex(IReadOnlyList<ColumnProfile> profile, Func<ColumnProfile, bool> predicate)
    {
        for (int i = 0; i < profile.Count; i++)
            if (predicate(profile[i])) return i;
        return -1;
    }

    // ----------------------------
    // Visual Aids (persist charts as chat attachments + embed markdown images)
    // ----------------------------
    private async Task<string> BuildVisualAidsMarkdownAsync(
        IReadOnlyList<ExcelChartArtifact> charts,
        string datasetName,
        CancellationToken ct)
    {
        if (charts is null || charts.Count == 0)
            return "";

        // Need a conversation id to store assistant attachments in the current chat.
        var conversationId = _session.ActiveConversationId ?? Guid.Empty;
        if (conversationId == Guid.Empty)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("### Visual Aids");
        sb.AppendLine();

        int i = 1;
        foreach (var ch in charts)
        {
            if (ch is null) continue;
            if (string.IsNullOrWhiteSpace(ch.Base64)) continue;

            byte[] bytes;
            try { bytes = Convert.FromBase64String(ch.Base64); }
            catch { continue; }

            if (bytes.Length == 0) continue;

            var contentType = string.IsNullOrWhiteSpace(ch.ContentType) ? "image/png" : ch.ContentType.Trim();
            var ext = contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg"
                : contentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase) ? "gif"
                : "png";

            var title = string.IsNullOrWhiteSpace(ch.Title) ? $"Chart {i}" : ch.Title.Trim();
            var safeTitle = SanitizeFileName(title);
            var fileName = string.IsNullOrWhiteSpace(ch.FileName)
                ? $"{SanitizeFileName(datasetName)}_{safeTitle}_{i}.{ext}"
                : SanitizeFileName(ch.FileName);

            if (!fileName.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
                fileName = Path.GetFileNameWithoutExtension(fileName) + "." + ext;

            // Save as assistant attachment INSIDE this conversation, so the chat UI can load /api/chat/attachments/{id}.
            var att = await _attachmentStore.SaveAssistantFileAsync(
                conversationId: conversationId,
                fileName: fileName,
                contentType: contentType,
                bytes: bytes,
                ct: ct);

            // Track in session so the UI "Attachments" panel shows it.
            _session.TrackAssistantAttachment(att);

            sb.AppendLine($"{i}. **{title}**");
            sb.AppendLine();
            sb.AppendLine($"![{title}]({att.StorageUrl})");
            sb.AppendLine();

            i++;
        }

        return sb.ToString().Trim();
    }

    private static string SanitizeFileName(string? name)
    {
        var n = string.IsNullOrWhiteSpace(name) ? "chart" : name.Trim();
        n = Regex.Replace(n, @"[^\w\.\-]+", "_"); // keep letters/digits/_/./-
        n = n.Trim('_');
        if (n.Length > 80) n = n.Substring(0, 80);
        if (string.IsNullOrWhiteSpace(n)) n = "chart";
        return n;
    }

    // ----------------------------
    // Evidence for LLM
    // ----------------------------
    private static string BuildEvidence(TabularData table, IReadOnlyList<IReadOnlyList<object?>> rows, List<ColumnProfile> profile)
    {
        var sb = new StringBuilder(16 * 1024);

        sb.AppendLine($"Dataset: {Safe(table.Name)}");
        sb.AppendLine($"Columns: {table.Columns.Count}");
        sb.AppendLine($"Rows: {rows.Count}");
        sb.AppendLine();
        sb.AppendLine("Column profile:");
        foreach (var p in profile)
            sb.AppendLine($"- {p.Name}: {p.Kind}, non-null {p.NonNull}, nulls {p.Nulls}");
        sb.AppendLine();

        // Sample rows (first 8)
        var take = Math.Min(8, rows.Count);
        if (take > 0)
        {
            sb.AppendLine("Sample rows (first rows, values truncated):");
            for (int i = 0; i < take; i++)
            {
                var row = rows[i];
                sb.Append($"Row {i + 1}: ");

                for (int c = 0; c < table.Columns.Count; c++)
                {
                    var v = c < row.Count ? row[c] : null;
                    var s = (v?.ToString() ?? "").Trim();
                    if (s.Length > 40) s = s.Substring(0, 40) + "…";
                    sb.Append($"{table.Columns[c]}={(string.IsNullOrWhiteSpace(s) ? "∅" : s)}");
                    if (c < table.Columns.Count - 1) sb.Append(" | ");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private async Task<string> RewriteToProfessionalMarkdownAsync(string question, string evidence, CancellationToken ct)
    {
        question = string.IsNullOrWhiteSpace(question) ? "Analyze the spreadsheet and provide insights." : question.Trim();

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System,
@"You are a spreadsheet analytics assistant.

Rules:
- Use ONLY the provided evidence.
- Do NOT invent numbers or columns.
- Produce concise, professional markdown.
- Provide: (1) Key insights (bullets), (2) Notable patterns/anomalies, (3) Suggested next cuts (optional).
- If evidence is insufficient, say what is missing."),
            new(ChatRole.User, $"Question:\n{question}\n\nEvidence:\n{evidence}")
        };

        var response = await _llm.GetResponseAsync(messages, cancellationToken: ct);

        var text = string.Concat(
            response.Messages
                .SelectMany(m => m.Contents)
                .OfType<TextContent>()
                .Select(t => t.Text));

        return (text ?? string.Empty).Trim();
    }

    private static string BuildFallbackMarkdown(TabularData table, IReadOnlyList<IReadOnlyList<object?>> rows, List<ColumnProfile> profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {Safe(table.Name)}");
        sb.AppendLine();
        sb.AppendLine($"- Rows: **{rows.Count}**");
        sb.AppendLine($"- Columns: **{table.Columns.Count}**");
        sb.AppendLine();
        sb.AppendLine("**Detected column types**");
        foreach (var p in profile)
            sb.AppendLine($"- {p.Name}: {p.Kind}");
        return sb.ToString();
    }

    // ----------------------------
    // Email-safe HTML
    // ----------------------------
    private static string BuildDenseHtmlSummary(TabularData table, IReadOnlyList<IReadOnlyList<object?>> rows, List<ColumnProfile> profile, IReadOnlyList<ExcelChartArtifact> charts)
    {
        // Dense, email-safe (tables + inline styles)
        var safeName = HtmlEncode(Safe(table.Name));

        var sb = new StringBuilder(16 * 1024);

        sb.AppendLine(@"<div style=""font-family:Segoe UI,Arial,sans-serif;font-size:13px;line-height:1.35;color:#111;"">");
        sb.AppendLine($@"  <div style=""font-size:16px;font-weight:600;margin:0 0 8px 0;"">{safeName}</div>");

        sb.AppendLine(@"  <table cellpadding=""0"" cellspacing=""0"" border=""0"" style=""border-collapse:collapse;margin:0 0 10px 0;"">");
        sb.AppendLine(@"    <tr>");
        sb.AppendLine($@"      <td style=""padding:4px 10px 4px 0;""><b>Rows</b>: {rows.Count}</td>");
        sb.AppendLine($@"      <td style=""padding:4px 10px 4px 0;""><b>Columns</b>: {table.Columns.Count}</td>");
        sb.AppendLine(@"    </tr>");
        sb.AppendLine(@"  </table>");

        sb.AppendLine(@"  <div style=""font-weight:600;margin:8px 0 4px 0;"">Column profile</div>");
        sb.AppendLine(@"  <table cellpadding=""0"" cellspacing=""0"" border=""0"" style=""border-collapse:collapse;width:100%;max-width:900px;"">");
        sb.AppendLine(@"    <tr>");
        sb.AppendLine(@"      <th align=""left"" style=""padding:4px 6px;border-bottom:1px solid #ddd;"">Column</th>");
        sb.AppendLine(@"      <th align=""left"" style=""padding:4px 6px;border-bottom:1px solid #ddd;"">Type</th>");
        sb.AppendLine(@"      <th align=""right"" style=""padding:4px 6px;border-bottom:1px solid #ddd;"">Non-null</th>");
        sb.AppendLine(@"      <th align=""right"" style=""padding:4px 6px;border-bottom:1px solid #ddd;"">Nulls</th>");
        sb.AppendLine(@"    </tr>");

        foreach (var p in profile.Take(30))
        {
            sb.AppendLine(@"    <tr>");
            sb.AppendLine($@"      <td style=""padding:3px 6px;border-bottom:1px solid #f0f0f0;"">{HtmlEncode(p.Name)}</td>");
            sb.AppendLine($@"      <td style=""padding:3px 6px;border-bottom:1px solid #f0f0f0;"">{p.Kind}</td>");
            sb.AppendLine($@"      <td align=""right"" style=""padding:3px 6px;border-bottom:1px solid #f0f0f0;"">{p.NonNull}</td>");
            sb.AppendLine($@"      <td align=""right"" style=""padding:3px 6px;border-bottom:1px solid #f0f0f0;"">{p.Nulls}</td>");
            sb.AppendLine(@"    </tr>");
        }
        sb.AppendLine(@"  </table>");

        // Preview rows
        var previewTake = Math.Min(6, rows.Count);
        if (previewTake > 0)
        {
            sb.AppendLine(@"  <div style=""font-weight:600;margin:10px 0 4px 0;"">Preview (first rows)</div>");
            sb.AppendLine(@"  <table cellpadding=""0"" cellspacing=""0"" border=""0"" style=""border-collapse:collapse;width:100%;max-width:900px;"">");

            // Header
            sb.AppendLine(@"    <tr>");
            foreach (var c in table.Columns.Take(12))
                sb.AppendLine($@"      <th align=""left"" style=""padding:4px 6px;border-bottom:1px solid #ddd;"">{HtmlEncode(c)}</th>");
            sb.AppendLine(@"    </tr>");

            for (int i = 0; i < previewTake; i++)
            {
                var row = rows[i];
                sb.AppendLine(@"    <tr>");
                for (int c = 0; c < Math.Min(12, table.Columns.Count); c++)
                {
                    var v = c < row.Count ? row[c] : null;
                    var s = (v?.ToString() ?? "").Trim();
                    if (s.Length > 40) s = s.Substring(0, 40) + "…";
                    sb.AppendLine($@"      <td style=""padding:3px 6px;border-bottom:1px solid #f0f0f0;"">{HtmlEncode(string.IsNullOrWhiteSpace(s) ? "∅" : s)}</td>");
                }
                sb.AppendLine(@"    </tr>");
            }
            sb.AppendLine(@"  </table>");
        }

        // Charts section is emitted as HTML placeholders (actual inline cids are handled by EmailChartHelpers)
        if (charts.Count > 0)
        {
            sb.AppendLine(@"  <div style=""font-weight:600;margin:12px 0 6px 0;"">Charts</div>");
            sb.AppendLine(@"  <div style=""color:#555;font-size:12px;margin:0 0 4px 0;"">Charts are provided as inline attachments (CID) when emailing.</div>");
            sb.AppendLine(@"  <ul style=""margin:0 0 0 18px;padding:0;"">");
            foreach (var ch in charts)
                sb.AppendLine($@"    <li style=""margin:0 0 2px 0;"">{HtmlEncode(ch.Title)} ({HtmlEncode(ch.FileName)})</li>");
            sb.AppendLine(@"  </ul>");
        }

        sb.AppendLine(@"</div>");
        return sb.ToString();
    }

    private static string BuildEmptyHtml(string message)
    {
        message = HtmlEncode(message ?? "No data.");
        return $@"<div style=""font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:#111;"">{message}</div>";
    }

    // ----------------------------
    // Parsing helpers
    // ----------------------------
    private static bool TryToDouble(object? v, out double d)
    {
        d = 0;
        if (v is null) return false;

        switch (v)
        {
            case double dd: d = dd; return true;
            case float ff: d = ff; return true;
            case decimal dec: d = (double)dec; return true;
            case int i: d = i; return true;
            case long l: d = l; return true;
            case short s: d = s; return true;
            case string str:
                str = str.Trim();
                return double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out d)
                    || double.TryParse(str, NumberStyles.Any, CultureInfo.CurrentCulture, out d);
            default:
                var asStr = v.ToString();
                if (string.IsNullOrWhiteSpace(asStr)) return false;
                return double.TryParse(asStr, NumberStyles.Any, CultureInfo.InvariantCulture, out d)
                    || double.TryParse(asStr, NumberStyles.Any, CultureInfo.CurrentCulture, out d);
        }
    }

    private static bool TryToDate(object? v, out DateTime dt)
    {
        dt = default;
        if (v is null) return false;

        if (v is DateTime dtt) { dt = dtt; return true; }
        if (v is DateOnly d0) { dt = d0.ToDateTime(TimeOnly.MinValue); return true; }

        if (v is double oa)
        {
            // Excel OADate sometimes stored as double
            try { dt = DateTime.FromOADate(oa); return true; } catch { }
        }

        var s = v.ToString();
        if (string.IsNullOrWhiteSpace(s)) return false;

        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt)
            || DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dt);
    }

    private static string Safe(string? s)
        => string.IsNullOrWhiteSpace(s) ? "Spreadsheet" : s.Trim();

    private static string HtmlEncode(string s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
}
