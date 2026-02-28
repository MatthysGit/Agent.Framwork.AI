using System.Text.Json;
using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Tools;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;

public sealed class ChatPipeline
{
    private readonly RouterAgent _router;
    private readonly AgentCallerTool _caller;
    private readonly ChatTools _chartTools;
    private readonly DocumentSearchTool _docSearchTool;
    private readonly DocumentEditTool _docEditTool;
    private readonly Func<Guid> _getConversationId;

    public ChatPipeline(
        RouterAgent router,
        AgentCallerTool caller,
        ChatTools chartTools,
        DocumentSearchTool docSearchTool,
        DocumentEditTool docEditTool,
        Func<Guid> getConversationId)
    {
        _router = router;
        _caller = caller;
        _chartTools = chartTools;
        _docSearchTool = docSearchTool;
        _docEditTool = docEditTool;
        _getConversationId = getConversationId;
    }

    public sealed record PipelineResult(string Text, string RoutedAgent, string RouterReason);

    public async Task<PipelineResult> ExecuteAsync(string userText, CancellationToken ct = default)
    {
        var r = await _router.RouteAsync(userText, ct);

        // Document search/edit are executed via tools so your attachment JSON stays identical.
        if (r.Agent.Equals(ChatAgentFactory.DocumentSearchAgentName, StringComparison.OrdinalIgnoreCase))
        {
            var convoId = _getConversationId();
            var json = await _docSearchTool.SearchDocumentsAsync(convoId, userText);
            return new PipelineResult(json ?? "No relevant information found.", r.Agent, r.Reason);
        }

        if (r.Agent.Equals(ChatAgentFactory.DocumentEditAgentName, StringComparison.OrdinalIgnoreCase))
        {
            var convoId = _getConversationId();
            var instruction = InferEditInstruction(userText);
            var json = await _docEditTool.EditDocumentAsync(convoId, userText, instruction);
            return new PipelineResult(json ?? "No edit result returned.", r.Agent, r.Reason);
        }

        // Chart path: SQL -> parse JSON -> chart tool -> markdown image only
        if (r.Mode.Equals("chart", StringComparison.OrdinalIgnoreCase))
        {
            var sql = await _caller.CallAgentAsync(ChatAgentFactory.SqlAgentName, userText, cancellationToken: ct);

            Console.WriteLine("SQL_AGENT_RAW_FOR_CHART:\n" + sql.Text);

            var chart = ParseChartJson(sql.Text);
            Console.WriteLine($"CHART_PARSED: type={chart.ChartType}, labels={(chart.Labels?.Count ?? 0)}, values={(chart.Values?.Count ?? 0)}, series={(chart.Series?.Count ?? 0)}");

            var url = await CreateChartAsync(chart);
            return new PipelineResult($"![chart]({url})", ChatAgentFactory.SqlAgentName, r.Reason);
        }

        // Default: call chosen agent
        var call = await _caller.CallAgentAsync(r.Agent, userText, cancellationToken: ct);
        return new PipelineResult(call.Text, call.AgentName, r.Reason);
    }

    private static string InferEditInstruction(string userText)
    {
        var lower = (userText ?? "").ToLowerInvariant();
        if (lower.Contains("comment")) return "add comments";
        if (lower.Contains("annotate")) return "annotate with comments";
        if (lower.Contains("highlight")) return "highlight issues and add comments";
        if (lower.Contains("proofread")) return "proofread and add comments";
        if (lower.Contains("revise") || lower.Contains("edit")) return "make suggested edits and add comments";
        return "add comments";
    }

    // Chart JSON schema support (native schema + SQL-agent schema normalization)
    private static ChartPayload ParseChartJson(string json)
    {
        // 1) Try direct deserialize into our expected payload.
        // IMPORTANT: Only accept if it contains renderable data; otherwise fall through to normalization.
        try
        {
            var direct = JsonSerializer.Deserialize<ChartPayload>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (direct is not null && !string.IsNullOrWhiteSpace(direct.ChartType))
            {
                var hasSingle =
                    direct.Labels is { Count: > 0 } &&
                    direct.Values is { Count: > 0 } &&
                    direct.Labels.Count == direct.Values.Count;

                var hasMulti =
                    direct.Labels is { Count: > 0 } &&
                    direct.Series is { Count: > 0 } &&
                    direct.Series.All(s => (s.Values?.Count ?? 0) == direct.Labels.Count);

                if (hasSingle || hasMulti)
                {
                    direct.ChartType = NormalizeChartType(direct.ChartType);
                    return direct;
                }
            }
        }
        catch
        {
            // fall through
        }

        // 2) Normalize SQL-agent schema (xis/xAxis.categories + series.data/values), case-insensitive property names.
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? chartTypeRaw = null;
            if (TryGetPropIgnoreCase(root, "chartType", out var ctEl) && ctEl.ValueKind == JsonValueKind.String)
                chartTypeRaw = ctEl.GetString();

            var chartType = NormalizeChartType(chartTypeRaw);

            var title = "Chart";
            if (TryGetPropIgnoreCase(root, "title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                title = titleEl.GetString() ?? "Chart";

            // x categories may be under "xAxis" OR "xis"
            JsonElement xObj = default;
            if (!(TryGetPropIgnoreCase(root, "xAxis", out xObj) || TryGetPropIgnoreCase(root, "xis", out xObj)))
                xObj = default;

            // labels
            var labels = new List<string>();
            if (xObj.ValueKind == JsonValueKind.Object &&
                TryGetPropIgnoreCase(xObj, "categories", out var catsEl) &&
                catsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in catsEl.EnumerateArray())
                    labels.Add(c.GetString() ?? "");
            }

            // x axis title
            var xAxisLabel = "";
            if (xObj.ValueKind == JsonValueKind.Object &&
                TryGetPropIgnoreCase(xObj, "title", out var xt) &&
                xt.ValueKind == JsonValueKind.String)
            {
                xAxisLabel = xt.GetString() ?? "";
            }

            // y axis title
            var yAxisLabel = "";
            if (TryGetPropIgnoreCase(root, "yAxis", out var yEl) && yEl.ValueKind == JsonValueKind.Object &&
                TryGetPropIgnoreCase(yEl, "title", out var yt) && yt.ValueKind == JsonValueKind.String)
            {
                yAxisLabel = yt.GetString() ?? "";
            }

            // series
            var seriesList = new List<ChartSeries>();
            if (TryGetPropIgnoreCase(root, "series", out var sEl) && sEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sEl.EnumerateArray())
                {
                    var name = "Series";
                    if (TryGetPropIgnoreCase(s, "name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                        name = nEl.GetString() ?? "Series";

                    // values may be "data" OR "values"
                    JsonElement dataEl = default;
                    if (!(TryGetPropIgnoreCase(s, "data", out dataEl) || TryGetPropIgnoreCase(s, "values", out dataEl)))
                        dataEl = default;

                    var vals = new List<double>();
                    if (dataEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in dataEl.EnumerateArray())
                            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
                                vals.Add(d);
                    }

                    seriesList.Add(new ChartSeries { Name = name, Values = vals });
                }
            }

            // If multiple series -> multicolumn; else single-series chart
            if (seriesList.Count > 1)
            {
                return new ChartPayload
                {
                    ChartType = "multicolumn",
                    Title = title,
                    XAxisLabel = xAxisLabel,
                    YAxisLabel = yAxisLabel,
                    Labels = labels,
                    Series = seriesList
                };
            }

            var single = seriesList.FirstOrDefault();
            return new ChartPayload
            {
                ChartType = chartType,
                Title = title,
                XAxisLabel = xAxisLabel,
                YAxisLabel = yAxisLabel,
                Labels = labels,
                Values = single?.Values ?? new List<double>()
            };
        }
        catch
        {
            return new ChartPayload
            {
                ChartType = "bar",
                Title = "Chart",
                Labels = new List<string> { "No data" },
                Values = new List<double> { 0 }
            };
        }
    }

    private static bool TryGetPropIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeChartType(string? t)
    {
        var s = (t ?? "").Trim().ToLowerInvariant();

        // SQL often returns "column" but your tool maps it to bar/column chart rendering
        return s switch
        {
            "column" => "bar",
            "columns" => "bar",
            "bar" => "bar",
            "pie" => "pie",
            "line" => "line",
            "area" => "area",
            "donut" => "donut",
            "gauge" => "gauge",
            "progress" => "progress",
            "multicolumn" => "multicolumn",
            _ => "bar"
        };
    }

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

    private Task<string> CreateChartAsync(ChartPayload p)
    {
        var t = (p.ChartType ?? "").Trim().ToLowerInvariant();

        var labels = (p.Labels ?? new List<string>()).ToArray();
        var values = (p.Values ?? new List<double>()).ToArray();

        // Validate or throw with a useful message (prevents "blank chart" silently)
        if (!t.Equals("multicolumn", StringComparison.OrdinalIgnoreCase) &&
            !t.Equals("gauge", StringComparison.OrdinalIgnoreCase))
        {
            EnsureValidSeries(labels, values, $"{Cap(t)} chart");
        }

        return t switch
        {
            "bar" => _chartTools.CreateBarChartPngAsync(
                p.Title ?? "Chart",
                p.XAxisLabel ?? "",
                p.YAxisLabel ?? "",
                labels,
                values),

            "pie" => _chartTools.CreatePieChartPngAsync(
                p.Title ?? "Chart",
                labels,
                values),

            "line" => _chartTools.CreateLineChartPngAsync(
                p.Title ?? "Chart",
                p.XAxisLabel ?? "",
                p.YAxisLabel ?? "",
                labels,
                values),

            "area" => _chartTools.CreateAreaChartPngAsync(
                p.Title ?? "Chart",
                p.XAxisLabel ?? "",
                p.YAxisLabel ?? "",
                labels,
                values),

            "donut" => _chartTools.CreateDonutChartPngAsync(
                p.Title ?? "Chart",
                labels,
                values),

            "gauge" => _chartTools.CreateGaugeChartPngAsync(
                p.Title ?? "Gauge",
                p.Value ?? 0,
                p.Min ?? 0,
                p.Max ?? 100),

            "progress" => _chartTools.CreateProgressBarsChartPngAsync(
                p.Title ?? "Progress",
                labels,
                values),

            "multicolumn" =>
                CreateMultiColumnAsync(p, labels),

            _ => _chartTools.CreateBarChartPngAsync(
                p.Title ?? "Chart",
                p.XAxisLabel ?? "",
                p.YAxisLabel ?? "",
                labels,
                values),
        };
    }

    private Task<string> CreateMultiColumnAsync(ChartPayload p, string[] labels)
    {
        var series = p.Series ?? new List<ChartSeries>();

        if (labels.Length == 0)
            throw new ArgumentException("Multi-column chart: No labels/categories to render.");

        if (series.Count == 0)
            throw new ArgumentException("Multi-column chart: No series to render.");

        var seriesNames = series.Select(s => s.Name ?? "Series").ToArray();
        var seriesValues = series.Select(s => (s.Values ?? new List<double>()).ToArray()).ToArray();

        for (int i = 0; i < seriesValues.Length; i++)
            EnsureValidSeries(labels, seriesValues[i], $"Multi-column {seriesNames[i]}");

        return _chartTools.CreateMultiSeriesColumnChartPngAsync(
            p.Title ?? "Chart",
            p.XAxisLabel ?? "",
            p.YAxisLabel ?? "",
            labels,
            seriesNames,
            seriesValues,
            width: 900,
            height: 500);
    }

    private static string Cap(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        if (s.Length == 1) return s.ToUpperInvariant();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    public sealed class ChartPayload
    {
        public string? ChartType { get; set; }
        public string? Title { get; set; }
        public string? XAxisLabel { get; set; }
        public string? YAxisLabel { get; set; }

        public List<string>? Labels { get; set; }
        public List<double>? Values { get; set; }

        public List<ChartSeries>? Series { get; set; }

        // gauge
        public double? Value { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
    }

    public sealed class ChartSeries
    {
        public string? Name { get; set; }
        public List<double>? Values { get; set; }
    }
}