using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Services.Chat.charts;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

public sealed class ChatTools
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ChatSession _session;
    private readonly ChatAttachmentStore _attachmentStore;
    private readonly BraveSearchClient _webSearch;

    // Existing
    private readonly PieChartRenderer _pie;

    // New renderers
    private readonly BarChartRenderer _bar;
    private readonly ColumnChartRenderer _column;
    private readonly MultiSeriesColumnChartRenderer _msColumn;
    private readonly LineChartRenderer _line;
    private readonly MultiSeriesLineChartRenderer _msLine;
    private readonly AreaChartRenderer _area;
    private readonly MultiSeriesAreaChartRenderer _msArea;
    private readonly DonutChartRenderer _donut;
    private readonly GaugeChartRenderer _gauge;
    private readonly ProgressBarsChartRenderer _progress;

    public ChatTools(
        IDbContextFactory<AppDbContext> dbFactory,
        ChatSession session,
        ChatAttachmentStore attachmentStore,
        BraveSearchClient webSearch,
        PieChartRenderer pie,

        // add these in DI
        BarChartRenderer bar,
        ColumnChartRenderer column,
        MultiSeriesColumnChartRenderer msColumn,
        LineChartRenderer line,
        MultiSeriesLineChartRenderer msLine,
        AreaChartRenderer area,
        MultiSeriesAreaChartRenderer msArea,
        DonutChartRenderer donut,
        GaugeChartRenderer gauge,
        ProgressBarsChartRenderer progress)
    {
        _dbFactory = dbFactory;
        _session = session;
        _attachmentStore = attachmentStore;
        _webSearch = webSearch;

        _pie = pie;

        _bar = bar;
        _column = column;
        _msColumn = msColumn;
        _line = line;
        _msLine = msLine;
        _area = area;
        _msArea = msArea;
        _donut = donut;
        _gauge = gauge;
        _progress = progress;
    }

    // ----------------------------
    // Shared tools (Chat Attachments)
    // ----------------------------

    [Description("Searches uploaded chat attachments using a phrase or keyword. Only searches files attached in the current conversation.")]
    public async Task<IEnumerable<string>> SearchAsync(
        [Description("The phrase to search for.")] string searchPhrase,
        [Description("If possible, specify the filename to search that file only. If not provided or empty, the search includes all files.")]
        string? filenameFilter = null)
    {
        if (!_session.ActiveConversationId.HasValue)
            return Array.Empty<string>();

        await using var db = await _dbFactory.CreateDbContextAsync();

        var q =
            from a in db.ChatMessageAttachments
            join m in db.ChatMessages on a.MessageId equals m.MessageId
            where m.ConversationId == _session.ActiveConversationId.Value
            select a;

        if (!string.IsNullOrWhiteSpace(filenameFilter))
            q = q.Where(a => a.FileName == filenameFilter);

        var attachments = await q
            .OrderByDescending(a => a.CreatedUtc)
            .Take(25)
            .ToListAsync();

        var results = new List<string>();

        foreach (var a in attachments)
        {
            if (results.Count >= 5) break;

            // NOTE: This is chat-attachment storage (likely disk-backed).
            // Your DB-backed DOCUMENT STORE (DocumentFiles) is handled by DocumentTool, not this tool.
            var path = _attachmentStore.TryResolveStoredPath(a.AttachmentId);
            if (path is null) continue;

            var ext = Path.GetExtension(a.FileName).ToLowerInvariant();
            var isText = ext is ".txt" or ".md" or ".csv" or ".json" or ".log";
            if (!isText) continue;

            string content;
            try
            {
                await using var fs = File.OpenRead(path);
                using var sr = new StreamReader(fs);

                var buffer = new char[512_000];
                var read = await sr.ReadBlockAsync(buffer, 0, buffer.Length);
                content = new string(buffer, 0, read);
            }
            catch
            {
                continue;
            }

            var idx = content.IndexOf(searchPhrase, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var start = Math.Max(0, idx - 120);
            var len = Math.Min(content.Length - start, 280);
            var snippet = content.Substring(start, len).Replace("\n", " ").Replace("\r", " ");

            results.Add($"<result filename=\"{a.FileName}\">{snippet}</result>");
        }

        return results;
    }

    // ----------------------------
    // Create attachments (NO DISK REQUIRED)
    // ----------------------------

    [Description("Creates a downloadable file in the current chat conversation and returns a download URL.")]
    public async Task<string> CreateChatFileAsync(
        [Description("File name to show in chat, e.g. report.csv")] string fileName,
        [Description("Mime type, e.g. text/csv or application/json")] string mimeType,
        [Description("File contents. Provide as base64 if isBase64 is true; otherwise plain text.")] string content,
        [Description("Set true if content is base64.")] bool isBase64 = false)
    {
        Console.WriteLine(">>> ChatTools.CreateChatFileAsync called");
        
        if (!_session.ActiveConversationId.HasValue)
            throw new InvalidOperationException("No active conversation.");

        var bytes = isBase64
            ? Convert.FromBase64String(content)
            : System.Text.Encoding.UTF8.GetBytes(content ?? string.Empty);

        var att = await _attachmentStore.SaveAssistantFileAsync(fileName, mimeType, bytes);
        _session.TrackAssistantAttachment(att);

        return att.StorageUrl;
    }

    [Description("Creates a downloadable file in the current chat from base64 content and returns a download URL. (DB-backed docs can use this; no disk required).")]
    public Task<string> CreateChatFileFromBase64Async(
        [Description("File name to show in chat, e.g. Example.pdf")] string fileName,
        [Description("Mime type, e.g. application/pdf")] string mimeType,
        [Description("Base64 encoded file content")] string base64Content)
        => CreateChatFileAsync(fileName, mimeType, base64Content, isBase64: true);

    // ----------------------------
    // Web search (Brave)
    // ----------------------------

    [Description("Searches the live web and returns a small set of results (title, url, snippet).")]
    public async Task<IEnumerable<string>> WebSearchAsync(
        [Description("Search query")] string query,
        [Description("Max results (1-10)")] int count = 5,
        [Description("Country code, e.g. US, GB, AE")] string country = "US",
        [Description("Search language, e.g. en")] string searchLang = "en")
        => await _webSearch.SearchAsync(query, count, country, searchLang);

    // ----------------------------
    // Chart generation (SkiaSharp)
    // ----------------------------

    private static void EnsureValidSeries(string[] labels, double[] values, string seriesName)
    {
        if (labels is null) throw new ArgumentNullException(nameof(labels));
        if (values is null) throw new ArgumentNullException(nameof(values));
        if (labels.Length == 0) throw new ArgumentException($"{seriesName}: No data to render.");
        if (labels.Length != values.Length) throw new ArgumentException($"{seriesName}: labels and values must have the same length.");

        for (int i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (double.IsNaN(v) || double.IsInfinity(v))
                throw new ArgumentException($"{seriesName}: value at index {i} is not a finite number.");
        }
    }

    private static void EnsureNonEmptyPng(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            throw new InvalidOperationException("Chart renderer returned an empty PNG payload.");
    }

    [Description("Creates a pie chart PNG and returns an attachment URL.")]
    public async Task<string> CreatePieChartPngAsync(
        [Description("Chart title")] string title,
        [Description("Slice labels (same length as values)")] string[] labels,
        [Description("Slice values (same length as labels)")] double[] values,
        [Description("Image width in pixels")] int width = 900,
        [Description("Image height in pixels")] int height = 600)
    {
        EnsureValidSeries(labels, values, "Pie chart");
        var bytes = _pie.RenderPng(title, labels, values, width, height);
        EnsureNonEmptyPng(bytes);
        var att = await _attachmentStore.SaveAssistantFileAsync("chart.png", "image/png", bytes);

        _session.TrackAssistantAttachment(att);
        return att.StorageUrl;
    }

    [Description("Creates a donut chart PNG and returns an attachment URL.")]
    public async Task<string> CreateDonutChartPngAsync(
        [Description("Chart title")] string title,
        [Description("Slice labels (same length as values)")] string[] labels,
        [Description("Slice values (same length as labels)")] double[] values,
        [Description("Image width in pixels")] int width = 900,
        [Description("Image height in pixels")] int height = 600)
    {
        EnsureValidSeries(labels, values, "Donut chart");
        var bytes = _donut.RenderPng(title, labels, values, width, height);
        EnsureNonEmptyPng(bytes);
        var att = await _attachmentStore.SaveAssistantFileAsync("chart.png", "image/png", bytes);

        _session.TrackAssistantAttachment(att);
        return att.StorageUrl;
    }

    [Description("Creates a bar chart PNG and returns an attachment URL (single series).")]
    public async Task<string> CreateBarChartPngAsync(
        [Description("Chart title")] string title,
        [Description("X axis label")] string xAxisLabel,
        [Description("Y axis label")] string yAxisLabel,
        [Description("Bar labels (same length as values)")] string[] labels,
        [Description("Bar values (same length as labels)")] double[] values,
        [Description("Image width in pixels")] int width = 1200,
        [Description("Image height in pixels")] int height = 700)
    {
        EnsureValidSeries(labels, values, "Bar chart");

        var ordered = labels.Zip(values, (l, v) => new { l, v })
                            .OrderByDescending(x => x.v)
                            .ToList();

        var finalLabels = ordered.Select(x => x.l).ToList();
        var finalValues = ordered.Select(x => x.v).ToList();

        var bytes = _bar.RenderPng(title, xAxisLabel, yAxisLabel, finalLabels, finalValues, width, height);
        EnsureNonEmptyPng(bytes);
        var att = await _attachmentStore.SaveAssistantFileAsync("chart.png", "image/png", bytes);

        _session.TrackAssistantAttachment(att);
        return att.StorageUrl;
    }

    [Description("Creates a column chart PNG and returns an attachment URL (single series).")]
    public async Task<string> CreateColumnChartPngAsync(
        [Description("Chart title")] string title,
        [Description("X axis label")] string xAxisLabel,
        [Description("Y axis label")] string yAxisLabel,
        [Description("Column labels (same length as values)")] string[] labels,
        [Description("Column values (same length as labels)")] double[] values,
        [Description("Image width in pixels")] int width = 1200,
        [Description("Image height in pixels")] int height = 700)
    {
        EnsureValidSeries(labels, values, "Column chart");
        var bytes = _column.RenderPng(title, xAxisLabel, yAxisLabel, labels, values, width, height);
        EnsureNonEmptyPng(bytes);
        var att = await _attachmentStore.SaveAssistantFileAsync("chart.png", "image/png", bytes);

        _session.TrackAssistantAttachment(att);
        return att.StorageUrl;
    }

    [Description("Creates a multi-series column chart PNG (grouped columns) and returns an attachment URL.")]
    public async Task<string> CreateMultiSeriesColumnChartPngAsync(
        [Description("Chart title")] string title,
        [Description("X axis label")] string xAxisLabel,
        [Description("Y axis label")] string yAxisLabel,
        [Description("X labels (categories)")] string[] xLabels,
        [Description("Series names")] string[] seriesNames,
        [Description("Series values matrix: seriesValues[seriesIndex][xIndex]")] double[][] seriesValues,
        [Description("Image width in pixels")] int width = 1300,
        [Description("Image height in pixels")] int height = 750)
    {
        ValidateMatrix(xLabels, seriesNames, seriesValues);

        var ro = seriesValues.Select(row => (IReadOnlyList<double>)row).ToList();
        var bytes = _msColumn.RenderPng(title, xAxisLabel, yAxisLabel, xLabels, seriesNames, ro, width, height);

        EnsureNonEmptyPng(bytes);

        var att = await _attachmentStore.SaveAssistantFileAsync("chart.png", "image/png", bytes);
        _session.TrackAssistantAttachment(att);
        return att.StorageUrl;
    }

    [Description("Creates a line chart PNG and returns an attachment URL (single series).")]
    public async Task<string> CreateLineChartPngAsync(
        [Description("Chart title")] string title,
        [Description("X axis label")] string xAxisLabel,
        [Description("Y axis label")] string yAxisLabel,
        [Description("X labels (same length as values)")] string[] xLabels,
        [Description("Y values (same length as xLabels)")] double[] yValues,
        [Description("Image width in pixels")] int width = 1200,
        [Description("Image height in pixels")] int height = 700)
    {
        EnsureValidSeries(xLabels, yValues, "Line chart");

        var bytes = _line.RenderPng(title, xAxisLabel, yAxisLabel, xLabels, yValues, width, height);
        EnsureNonEmptyPng(bytes);
        var att = await _attachmentStore.SaveAssistantFileAsync("chart.png", "image/png", bytes);

        _session.TrackAssistantAttachment(att);
        return att.StorageUrl;
    }

    [Description("Creates a multi-series line chart PNG and returns an attachment URL.")]
    public async Task<string> CreateMultiSeriesLineChartPngAsync(
        [Description("Chart title")] string title,
        [Description("X axis label")] string xAxisLabel,
        [Description("Y axis label")] string yAxisLabel,
        [Description("X labels (categories)")] string[] xLabels,
        [Description("Series names")] string[] seriesNames,
        [Description("Series values matrix: seriesValues[seriesIndex][xIndex]")] double[][] seriesValues,
        [Description("Image width in pixels")] int width = 1300,
        [Description("Image height in pixels")] int height = 750)
    {
        ValidateMatrix(xLabels, seriesNames, seriesValues);

        var ro = seriesValues.Select(row => (IReadOnlyList<double>)row).ToList();
        var bytes = _msLine.RenderPng(title, xAxisLabel, yAxisLabel, xLabels, seriesNames, ro, width, height);

        EnsureNonEmptyPng(bytes);

        var att = await _attachmentStore.SaveAssistantFileAsync("chart.png", "image/png", bytes);
        _session.TrackAssistantAttachment(att);
        return att.StorageUrl;
    }

    [Description("Creates an area chart PNG and returns an attachment URL (single series).")]
    public async Task<string> CreateAreaChartPngAsync(
        [Description("Chart title")] string title,
        [Description("X axis label")] string xAxisLabel,
        [Description("Y axis label")] string yAxisLabel,
        [Description("X labels (same length as values)")] string[] xLabels,
        [Description("Y values (same length as xLabels)")] double[] yValues,
        [Description("Image width in pixels")] int width = 1200,
        [Description("Image height in pixels")] int height = 700)
    {
        EnsureValidSeries(xLabels, yValues, "Area chart");

        var bytes = _area.RenderPng(title, xAxisLabel, yAxisLabel, xLabels, yValues, width, height);
        EnsureNonEmptyPng(bytes);
        var att = await _attachmentStore.SaveAssistantFileAsync("chart.png", "image/png", bytes);

        _session.TrackAssistantAttachment(att);
        return att.StorageUrl;
    }

    [Description("Creates a multi-series area chart PNG and returns an attachment URL.")]
    public async Task<string> CreateMultiSeriesAreaChartPngAsync(
        [Description("Chart title")] string title,
        [Description("X axis label")] string xAxisLabel,
        [Description("Y axis label")] string yAxisLabel,
        [Description("X labels (categories)")] string[] xLabels,
        [Description("Series names")] string[] seriesNames,
        [Description("Series values matrix: seriesValues[seriesIndex][xIndex]")] double[][] seriesValues,
        [Description("Image width in pixels")] int width = 1300,
        [Description("Image height in pixels")] int height = 750)
    {
        ValidateMatrix(xLabels, seriesNames, seriesValues);

        var ro = seriesValues.Select(row => (IReadOnlyList<double>)row).ToList();
        var bytes = _msArea.RenderPng(title, xAxisLabel, yAxisLabel, xLabels, seriesNames, ro, width, height);

        EnsureNonEmptyPng(bytes);

        var att = await _attachmentStore.SaveAssistantFileAsync("chart.png", "image/png", bytes);
        _session.TrackAssistantAttachment(att);
        return att.StorageUrl;
    }

    [Description("Creates a gauge chart PNG and returns an attachment URL.")]
    public async Task<string> CreateGaugeChartPngAsync(
        [Description("Chart title")] string title,
        [Description("Value")] double value,
        [Description("Minimum")] double min = 0,
        [Description("Maximum")] double max = 100,
        [Description("Image width in pixels")] int width = 900,
        [Description("Image height in pixels")] int height = 500)
    {
        var bytes = _gauge.RenderPng(title, value, min, max, width, height);
        EnsureNonEmptyPng(bytes);
        var att = await _attachmentStore.SaveAssistantFileAsync("chart.png", "image/png", bytes);

        _session.TrackAssistantAttachment(att);
        return att.StorageUrl;
    }

    [Description("Creates a progress bars chart PNG and returns an attachment URL.")]
    public async Task<string> CreateProgressBarsChartPngAsync(
        [Description("Chart title")] string title,
        [Description("Item labels (same length as values)")] string[] labels,
        [Description("Item values (same length as labels)")] double[] values,
        [Description("Max value for 100%")] double maxValue = 100,
        [Description("Image width in pixels")] int width = 1000,
        [Description("Image height in pixels")] int height = 600)
    {
        EnsureValidSeries(labels, values, "Progress bars chart");

        var bytes = _progress.RenderPng(title, labels, values, maxValue, width, height);
        EnsureNonEmptyPng(bytes);
        var att = await _attachmentStore.SaveAssistantFileAsync("chart.png", "image/png", bytes);

        _session.TrackAssistantAttachment(att);
        return att.StorageUrl;
    }

    private static void ValidateMatrix(string[] xLabels, string[] seriesNames, double[][] seriesValues)
    {
        if (xLabels is null || xLabels.Length == 0) throw new ArgumentException("xLabels required.");
        if (seriesNames is null || seriesNames.Length == 0) throw new ArgumentException("seriesNames required.");
        if (seriesValues is null || seriesValues.Length != seriesNames.Length) throw new ArgumentException("seriesValues must match seriesNames length.");

        for (int i = 0; i < seriesValues.Length; i++)
        {
            if (seriesValues[i] is null || seriesValues[i].Length != xLabels.Length)
                throw new ArgumentException($"seriesValues[{i}] length must match xLabels length.");

            for (int j = 0; j < seriesValues[i].Length; j++)
            {
                var v = seriesValues[i][j];
                if (double.IsNaN(v) || double.IsInfinity(v))
                    throw new ArgumentException($"seriesValues[{i}][{j}] is not a finite number.");
            }
        }
    }
}