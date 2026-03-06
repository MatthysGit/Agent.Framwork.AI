using System.Text.Json;
using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly;

public static class AnomalyInputParser
{
    public static bool TryParse(string text, string metricLabel, string? groupBy, out AnomalyDataset dataset)
    {
        dataset = new AnomalyDataset();
        var json = ExtractFirstJsonObject(text) ?? (LooksLikeJson(text) ? text : null);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetPropIgnoreCase(doc.RootElement, "rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                return false;

            var points = new List<AnomalyInputPoint>();
            foreach (var row in rowsEl.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;

                if (!TryGetPropIgnoreCase(row, "Period", out var periodEl))
                    continue;
                if (!TryGetPropIgnoreCase(row, "Value", out var valueEl))
                    continue;

                var periodText = periodEl.ValueKind == JsonValueKind.String ? (periodEl.GetString() ?? string.Empty) : periodEl.ToString();
                if (!DateTime.TryParse(periodText, out var period))
                    continue;

                double value;
                if (valueEl.ValueKind == JsonValueKind.Number && valueEl.TryGetDouble(out var num))
                    value = num;
                else if (valueEl.ValueKind == JsonValueKind.String && double.TryParse(valueEl.GetString(), out var parsed))
                    value = parsed;
                else
                    continue;

                string? series = null;
                if (TryGetPropIgnoreCase(row, "Series", out var seriesEl))
                    series = seriesEl.ValueKind == JsonValueKind.String ? seriesEl.GetString() : seriesEl.ToString();

                var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in row.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "Period", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(prop.Name, "Value", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(prop.Name, "Series", StringComparison.OrdinalIgnoreCase))
                        continue;
                    attrs[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? (prop.Value.GetString() ?? string.Empty) : prop.Value.ToString();
                }

                points.Add(new AnomalyInputPoint
                {
                    Period = period,
                    Value = value,
                    Series = string.IsNullOrWhiteSpace(series) ? null : series,
                    Attributes = attrs
                });
            }

            if (points.Count == 0)
                return false;

            var seriesGroups = points
                .GroupBy(p => string.IsNullOrWhiteSpace(p.Series) ? "Overall" : p.Series!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new AnomalySeries
                {
                    Name = g.Key,
                    Points = g.OrderBy(p => p.Period).ToList()
                })
                .OrderBy(s => s.Name)
                .ToList();

            dataset = new AnomalyDataset
            {
                MetricLabel = metricLabel,
                GroupBy = groupBy,
                IsMultiSeries = seriesGroups.Count > 1,
                Series = seriesGroups
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeJson(string? text)
        => !string.IsNullOrWhiteSpace(text) && (text!.TrimStart().StartsWith("{") || text.TrimStart().StartsWith("["));

    private static string? ExtractFirstJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        var start = s.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escape = false;
        for (int i = start; i < s.Length; i++)
        {
            var ch = s[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (ch == '\\') escape = true;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') { inString = true; continue; }
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return s[start..(i + 1)];
            }
        }
        return null;
    }

    private static bool TryGetPropIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
