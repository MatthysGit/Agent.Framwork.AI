using System.Globalization;
using System.Text;
using System.Text.Json;
using Ai.AgentFramwork.Massar.Web.Services.WhatIfs.Modelss;


namespace Ai.AgentFramwork.Massar.Web.Services.WhatIfs;


public static class WhatIfSimulationService
{
    public static WhatIfRequest ParseRequest(string userText)
        => WhatIfRequest.Parse(userText);

    public static bool TryParseInputPoints(string text, out List<WhatIfInputPoint> points)
    {
        points = new List<WhatIfInputPoint>();

        var json = ExtractFirstJsonObject(text) ?? (LooksLikeJson(text) ? text : null);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetPropIgnoreCase(doc.RootElement, "rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var row in rowsEl.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;

                if (!TryResolveLabel(row, out var label, out var sortDate))
                    continue;

                if (!TryResolveValue(row, out var value))
                    continue;

                string? series = null;
                if (TryResolveSeries(row, out var s))
                    series = s;

                points.Add(new WhatIfInputPoint(label, value, series, sortDate));
            }

            if (points.Count == 0)
                return false;

            points = points
                .OrderBy(p => p.Series ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.SortDate ?? DateTime.MaxValue)
                .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static WhatIfSimulationResult Run(WhatIfRequest request, List<WhatIfInputPoint> inputPoints)
    {
        var multiplier = 1d + (request.ChangePercent / 100d);

        var simulated = inputPoints
            .Select(p =>
            {
                var scenario = Math.Round(p.Value * multiplier, 4);
                var delta = Math.Round(scenario - p.Value, 4);
                var deltaPct = p.Value == 0 ? 0d : Math.Round((delta / p.Value) * 100d, 4);

                return new WhatIfSimulationPoint(
                    Label: p.Label,
                    BaselineValue: Math.Round(p.Value, 4),
                    ScenarioValue: scenario,
                    DeltaValue: delta,
                    DeltaPct: deltaPct,
                    Series: p.Series,
                    SortDate: p.SortDate);
            })
            .ToList();

        var baselineTotal = simulated.Sum(p => p.BaselineValue);
        var scenarioTotal = simulated.Sum(p => p.ScenarioValue);
        var totalDelta = scenarioTotal - baselineTotal;
        var totalDeltaPct = baselineTotal == 0 ? 0d : (totalDelta / baselineTotal) * 100d;

        var grouped = simulated.Any(p => !string.IsNullOrWhiteSpace(p.Series));
        var timeSeriesLike = simulated.Count > 1 && simulated.Count(p => p.SortDate.HasValue) >= Math.Max(2, simulated.Count / 2);

        var assumptions = BuildAssumptions(request, grouped, timeSeriesLike);

        var summary = BuildSummary(request, baselineTotal, scenarioTotal, totalDelta, totalDeltaPct, simulated);

        return new WhatIfSimulationResult
        {
            Title = BuildTitle(request),
            Summary = summary,
            MetricLabel = request.MetricName,
            ScenarioLabel = request.ScenarioName,
            VariableName = request.VariableName,
            ChangePercent = request.ChangePercent,
            Grouped = grouped,
            TimeSeriesLike = timeSeriesLike,
            Assumptions = assumptions,
            Points = simulated
        };
    }

    public static string BuildMarkdown(WhatIfSimulationResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("What-If Simulation Summary");
        sb.AppendLine(result.Summary);
        sb.AppendLine();

        if (result.Assumptions.Count > 0)
        {
            sb.AppendLine("Assumptions");
            foreach (var item in result.Assumptions)
                sb.AppendLine($"• {item}");
            sb.AppendLine();
        }

        var rows = result.Points
            .OrderBy(p => p.Series ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.SortDate ?? DateTime.MaxValue)
            .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToList();

        sb.AppendLine("| Series | Label | Baseline | Scenario | Delta | Delta % |");
        sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: |");
        foreach (var p in rows)
        {
            sb.AppendLine($"| {EscapeMd(p.Series ?? "Overall")} | {EscapeMd(p.Label)} | {FormatValue(p.BaselineValue)} | {FormatValue(p.ScenarioValue)} | {FormatSignedValue(p.DeltaValue)} | {p.DeltaPct:0.##}% |");
        }

        if (result.Points.Count > rows.Count)
            sb.AppendLine($"\nShowing first {rows.Count} rows of {result.Points.Count} total simulation rows.");

        return sb.ToString().Trim();
    }

    public static string BuildHiddenPayload(WhatIfSimulationResult result)
    {
        return JsonSerializer.Serialize(new
        {
            type = "whatif_result",
            title = result.Title,
            summary = result.Summary,
            metric = result.MetricLabel,
            scenario = result.ScenarioLabel,
            variable = result.VariableName,
            changePercent = result.ChangePercent,
            grouped = result.Grouped,
            timeSeriesLike = result.TimeSeriesLike,
            points = result.Points.Select(p => new
            {
                label = p.Label,
                baseline = p.BaselineValue,
                scenario = p.ScenarioValue,
                delta = p.DeltaValue,
                deltaPct = p.DeltaPct,
                series = p.Series,
                sortDate = p.SortDate
            }).ToList()
        });
    }

    private static List<string> BuildAssumptions(WhatIfRequest request, bool grouped, bool timeSeriesLike)
    {
        var assumptions = new List<string>
        {
            $"A uniform {Math.Abs(request.ChangePercent):0.##}% {(request.ChangePercent >= 0 ? "increase" : "decrease")} was applied to the baseline values.",
            $"The simulation assumes all other drivers remain unchanged unless explicitly stated in the prompt.",
            $"This is a sensitivity-style estimate, not a causal forecast."
        };

        if (grouped)
            assumptions.Add("The same adjustment was applied consistently across all returned series/groups.");

        if (timeSeriesLike)
            assumptions.Add("Historical pattern ordering was preserved and only the scenario values were adjusted.");

        return assumptions;
    }

    private static string BuildSummary(WhatIfRequest request, double baselineTotal, double scenarioTotal, double totalDelta, double totalDeltaPct, List<WhatIfSimulationPoint> points)
    {
        var largest = points.OrderByDescending(p => Math.Abs(p.DeltaValue)).FirstOrDefault();
        var lead = request.ChangePercent >= 0
            ? $"Applying **{Math.Abs(request.ChangePercent):0.##}%** upside to **{request.VariableName}** changes the total **{request.MetricName}** from **{FormatValue(baselineTotal)}** to **{FormatValue(scenarioTotal)}**."
            : $"Applying **{Math.Abs(request.ChangePercent):0.##}%** downside to **{request.VariableName}** changes the total **{request.MetricName}** from **{FormatValue(baselineTotal)}** to **{FormatValue(scenarioTotal)}**.";

        var deltaSentence = $"That is a **{FormatSignedValue(totalDelta)}** change, or **{totalDeltaPct:0.##}%** versus the baseline.";

        if (largest is null)
            return lead + " " + deltaSentence;

        var largestLabel = string.IsNullOrWhiteSpace(largest.Series)
            ? largest.Label
            : $"{largest.Series} / {largest.Label}";

        var impactSentence = $"The largest modeled shift is on **{EscapeMd(largestLabel)}**, with a delta of **{FormatSignedValue(largest.DeltaValue)}**.";

        return $"{lead} {deltaSentence} {impactSentence}";
    }

    private static string BuildTitle(WhatIfRequest request)
        => $"{request.MetricName} What-If: {request.ScenarioName}";

    private static bool TryResolveLabel(JsonElement row, out string label, out DateTime? sortDate)
    {
        sortDate = null;

        foreach (var key in new[] { "Label", "Period", "Bucket", "Category", "Segment", "Name", "Month", "Date" })
        {
            if (!TryGetPropIgnoreCase(row, key, out var el))
                continue;

            label = el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.ToString();
            if (string.IsNullOrWhiteSpace(label))
                continue;

            if (DateTime.TryParse(label, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                sortDate = dt;

            return true;
        }

        label = "";
        return false;
    }

    private static bool TryResolveValue(JsonElement row, out double value)
    {
        foreach (var key in new[] { "Value", "Amount", "Metric", "Total", "Sales", "Revenue", "Cost", "Profit", "Margin", "Orders", "Headcount", "Count" })
        {
            if (TryGetPropIgnoreCase(row, key, out var el) && TryGetDoubleValue(el, out value))
                return true;
        }

        foreach (var p in row.EnumerateObject())
        {
            if (TryGetDoubleValue(p.Value, out value))
                return true;
        }

        value = 0d;
        return false;
    }

    private static bool TryResolveSeries(JsonElement row, out string series)
    {
        foreach (var key in new[] { "Series", "Group", "Region", "Territory", "Department", "Channel", "Type" })
        {
            if (TryGetPropIgnoreCase(row, key, out var el))
            {
                series = el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.ToString();
                return !string.IsNullOrWhiteSpace(series);
            }
        }

        series = "";
        return false;
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

    private static bool TryGetDoubleValue(JsonElement el, out double value)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value))
            return true;

        if (el.ValueKind == JsonValueKind.String)
        {
            var s = (el.GetString() ?? "").Replace(",", "").Replace("%", "").Trim();
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ||
                double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
                return true;
        }

        value = 0d;
        return false;
    }

    private static bool LooksLikeJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.TrimStart();
        return t.StartsWith("{") || t.StartsWith("[");
    }

    private static string? ExtractFirstJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var s = text.Trim()
            .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "", StringComparison.OrdinalIgnoreCase);

        var start = s.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        for (var i = start; i < s.Length; i++)
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

    private static string EscapeMd(string text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Replace("|", "\\|").Trim();

    private static string FormatValue(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 0.0000001d)
            return Math.Round(value).ToString("0");

        return value.ToString("0.##");
    }

    private static string FormatSignedValue(double value)
        => value >= 0 ? $"+{FormatValue(value)}" : $"-{FormatValue(Math.Abs(value))}";
}
