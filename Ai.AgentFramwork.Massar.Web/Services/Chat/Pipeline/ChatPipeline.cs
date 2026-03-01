using System.Text;
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
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var swRoute = System.Diagnostics.Stopwatch.StartNew();

        var r = await _router.RouteAsync(userText, ct);

        swRoute.Stop();
        Console.WriteLine(
            $"[ROUTER] mode={r.Mode} agent={r.Agent} chartType={r.ChartType ?? "null"} " +
            $"reason=\"{r.Reason}\" routeMs={swRoute.ElapsedMilliseconds}");

        // Document search/edit are executed via tools so your attachment JSON stays identical.
        if (r.Agent.Equals(ChatAgentFactory.DocumentSearchAgentName, StringComparison.OrdinalIgnoreCase))
        {
            var convoId = _getConversationId();
            var json = await _docSearchTool.SearchDocumentsAsync(convoId, userText);
            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={r.Agent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");
            return new PipelineResult(json ?? "No relevant information found.", r.Agent, r.Reason);
        }

        if (r.Agent.Equals(ChatAgentFactory.DocumentEditAgentName, StringComparison.OrdinalIgnoreCase))
        {
            var convoId = _getConversationId();
            var instruction = InferEditInstruction(userText);
            var json = await _docEditTool.EditDocumentAsync(convoId, userText, instruction);
            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={r.Agent} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");
            return new PipelineResult(json ?? "No edit result returned.", r.Agent, r.Reason);
        }

        // Chart path: SQL -> parse JSON -> chart tool -> markdown image only
        // With automatic fallback: if chart validation fails -> ask SQL agent for a table JSON -> render markdown table
        if (r.Mode.Equals("chart", StringComparison.OrdinalIgnoreCase))
        {
            // ✅ HARD ENFORCEMENT: Always wrap the SQL call so output is strict schema
            var desired = string.IsNullOrWhiteSpace(r.ChartType) ? "column" : r.ChartType!.Trim().ToLowerInvariant();
            // Router returns "bar" etc; our enforced schema uses "column" which we normalize -> bar
            if (desired == "bar") desired = "column";

            var sqlPrompt =
                "You are the SQL agent. The user wants a CHART.\n\n" +
                "Return ONLY strict JSON (no markdown, no prose, no code fences).\n" +
                "Use EXACTLY this schema (all fields required):\n\n" +
                string.Format(@"
{{
  ""chartType"": ""{0}"",
  ""title"": ""<short title>"",
  ""xAxis"": {{
    ""title"": ""<x axis title>"",
    ""categories"": [""A"",""B"",""C""]
  }},
  ""yAxis"": {{
    ""title"": ""<y axis title>""
  }},
  ""series"": [
    {{
      ""name"": ""<series name>"",
      ""data"": [1,2,3]
    }}
  ]
}}

Rules:
- categories length MUST equal each series[i].data length
- series[].data values MUST be numbers (not strings)
- If no data, return:
{{
  ""chartType"": ""column"",
  ""title"": ""No data"",
  ""xAxis"": {{ ""title"": """", ""categories"": [""No data""] }},
  ""yAxis"": {{ ""title"": """" }},
  ""series"": [{{ ""name"": ""No data"", ""data"": [0] }}]
}}

User request:
{1}
", desired, userText);

            var sql = await _caller.CallAgentAsync(ChatAgentFactory.SqlAgentName, sqlPrompt, cancellationToken: ct);

            Console.WriteLine("SQL_AGENT_RAW_FOR_CHART:\n" + sql.Text);

            // ✅ Strip markdown / extra text drift and keep the JSON object
            var jsonOnly = ExtractFirstJsonObject(sql.Text) ?? sql.Text;

            try
            {
                var chart = ParseChartJson(jsonOnly);
                Console.WriteLine($"CHART_PARSED: type={chart.ChartType}, labels={(chart.Labels?.Count ?? 0)}, values={(chart.Values?.Count ?? 0)}, series={(chart.Series?.Count ?? 0)}");

                var url = await CreateChartAsync(chart);

                swTotal.Stop();
                Console.WriteLine($"[PIPELINE] done agent={ChatAgentFactory.SqlAgentName} mode=chart totalMs={swTotal.ElapsedMilliseconds}");

                return new PipelineResult($"![chart]({url})", ChatAgentFactory.SqlAgentName, r.Reason);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[CHART_FALLBACK] Chart failed: " + ex);

                var tablePrompt = string.Format(@"
The user asked for a chart, but chart rendering failed.

Return ONLY JSON in this schema (no markdown, no prose):
{{
  ""title"": ""<short title>"",
  ""columns"": [""Col1"",""Col2""],
  ""rows"": [
    [""A"",""1""],
    [""B"",""2""]
  ]
}}

User request:
{0}
", userText);

                var tableJson = await _caller.CallAgentAsync(ChatAgentFactory.SqlAgentName, tablePrompt, cancellationToken: ct);

                var tableJsonOnly = ExtractFirstJsonObject(tableJson.Text) ?? tableJson.Text;

                var fallbackMd = TryRenderTableMarkdown(tableJsonOnly, out var tableMd)
                    ? tableMd
                    : $"Chart generation failed and table fallback could not be generated.\n\nError: {ex.Message}";

                // Run PPI on fallback markdown (helps catch number inflation + safety)
                var ppiPrompt = $"""
USER:
{userText}

DRAFT ANSWER:
{fallbackMd}

Return the final answer text only.
""";

                var checkedFallback = await _caller.CallAgentAsync(ChatAgentFactory.PpiAgentName, ppiPrompt, cancellationToken: ct);

                swTotal.Stop();
                Console.WriteLine($"[PIPELINE] done agent={ChatAgentFactory.SqlAgentName} mode=chart-fallback totalMs={swTotal.ElapsedMilliseconds}");

                return new PipelineResult(checkedFallback.Text, ChatAgentFactory.SqlAgentName, $"{r.Reason} (chart->table fallback)");
            }
        }

        // Default: call chosen agent
        var call = await _caller.CallAgentAsync(r.Agent, userText, cancellationToken: ct);

        // Skip PPI for doc tools that must return JSON unchanged
        if (r.Agent.Equals(ChatAgentFactory.DocumentSearchAgentName, StringComparison.OrdinalIgnoreCase) ||
            r.Agent.Equals(ChatAgentFactory.DocumentEditAgentName, StringComparison.OrdinalIgnoreCase))
        {
            swTotal.Stop();
            Console.WriteLine($"[PIPELINE] done agent={call.AgentName} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");
            return new PipelineResult(call.Text, call.AgentName, r.Reason);
        }

        // Run PPI
        var ppiPromptNormal = $"""
USER:
{userText}

DRAFT ANSWER:
{call.Text}

Return the final answer text only.
""";

        var checkedAnswer = await _caller.CallAgentAsync(ChatAgentFactory.PpiAgentName, ppiPromptNormal, cancellationToken: ct);

        swTotal.Stop();
        Console.WriteLine($"[PIPELINE] done agent={call.AgentName} mode={r.Mode} totalMs={swTotal.ElapsedMilliseconds}");

        return new PipelineResult(checkedAnswer.Text, call.AgentName, r.Reason);
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

    // ✅ Extract the first {...} JSON object (handles code fences / extra prose)
    private static string? ExtractFirstJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var s = text.Trim();

        // remove common code fences
        s = s.Replace("```json", "", StringComparison.OrdinalIgnoreCase)
             .Replace("```", "");

        var start = s.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return s.Substring(start, i - start + 1);
            }
        }

        return null;
    }

    // Chart JSON schema support (native schema + SQL-agent schema normalization)
    private static ChartPayload ParseChartJson(string json)
    {
        // 1) Try direct deserialize into our expected payload.
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

        // 2) Normalize SQL-agent schema (xAxis/xis.categories + series.data), case-insensitive property names.
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

            // xAxis or xis
            JsonElement xObj = default;
            if (!(TryGetPropIgnoreCase(root, "xAxis", out xObj) || TryGetPropIgnoreCase(root, "xis", out xObj)))
                xObj = default;

            var labels = new List<string>();
            if (xObj.ValueKind == JsonValueKind.Object &&
                TryGetPropIgnoreCase(xObj, "categories", out var catsEl) &&
                catsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in catsEl.EnumerateArray())
                    labels.Add(c.GetString() ?? "");
            }

            var xAxisLabel = "";
            if (xObj.ValueKind == JsonValueKind.Object &&
                TryGetPropIgnoreCase(xObj, "title", out var xt) &&
                xt.ValueKind == JsonValueKind.String)
            {
                xAxisLabel = xt.GetString() ?? "";
            }

            var yAxisLabel = "";
            if (TryGetPropIgnoreCase(root, "yAxis", out var yEl) && yEl.ValueKind == JsonValueKind.Object &&
                TryGetPropIgnoreCase(yEl, "title", out var yt))
            {
                yAxisLabel = yt.ValueKind == JsonValueKind.String ? (yt.GetString() ?? "") : yt.ToString();
            }

            var seriesList = new List<ChartSeries>();
            if (TryGetPropIgnoreCase(root, "series", out var sEl) && sEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sEl.EnumerateArray())
                {
                    var name = "Series";
                    if (TryGetPropIgnoreCase(s, "name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                        name = nEl.GetString() ?? "Series";

                    // data preferred, values fallback
                    JsonElement dataEl = default;
                    if (!(TryGetPropIgnoreCase(s, "data", out dataEl) || TryGetPropIgnoreCase(s, "values", out dataEl)))
                        dataEl = default;

                    var vals = new List<double>();
                    if (dataEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in dataEl.EnumerateArray())
                        {
                            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
                                vals.Add(d);
                            else if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var ds))
                                vals.Add(ds);
                        }
                    }

                    seriesList.Add(new ChartSeries { Name = name, Values = vals });
                }
            }

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

    private static bool TryRenderTableMarkdown(string json, out string markdown)
    {
        markdown = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? title = null;
            if (TryGetPropIgnoreCase(root, "title", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                title = tEl.GetString();

            if (!TryGetPropIgnoreCase(root, "columns", out var colsEl) || colsEl.ValueKind != JsonValueKind.Array)
                return false;

            if (!TryGetPropIgnoreCase(root, "rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                return false;

            var cols = colsEl.EnumerateArray().Select(c => c.GetString() ?? c.ToString()).ToArray();
            if (cols.Length == 0) return false;

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title))
                sb.AppendLine($"**{title}**\n");

            sb.AppendLine("| " + string.Join(" | ", cols) + " |");
            sb.AppendLine("| " + string.Join(" | ", cols.Select(_ => "---")) + " |");

            foreach (var rowEl in rowsEl.EnumerateArray())
            {
                if (rowEl.ValueKind != JsonValueKind.Array) continue;
                var row = rowEl.EnumerateArray().Select(c => c.GetString() ?? c.ToString()).ToArray();

                var cells = row.Length < cols.Length
                    ? row.Concat(Enumerable.Repeat("", cols.Length - row.Length)).ToArray()
                    : row.Take(cols.Length).ToArray();

                sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            }

            markdown = sb.ToString();
            return true;
        }
        catch
        {
            return false;
        }
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