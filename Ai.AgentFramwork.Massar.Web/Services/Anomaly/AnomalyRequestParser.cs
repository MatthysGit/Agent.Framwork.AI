using System.Text.RegularExpressions;
using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly;

public static class AnomalyRequestParser
{
    public static AnomalyDetectionRequest Parse(string userText)
    {
        var text = (userText ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();

        var metric = InferMetric(lower);
        var groupBy = InferGroupBy(lower);
        var lookback = ExtractNumberNear(lower, "last", defaultValue: 24);
        var forecastPeriods = ExtractNumberNear(lower, "next", defaultValue: 6);
        var topN = ExtractTopN(lower);
        var sensitivity = InferSensitivity(lower);
        var detectChangePoints = lower.Contains("change point") || lower.Contains("structural") || lower.Contains("shift") || lower.Contains("break");
        var compareForecast = lower.Contains("forecast") || lower.Contains("expected") || lower.Contains("prediction band");
        var driver = lower.Contains("driver") || lower.Contains("cause") || lower.Contains("why") || lower.Contains("contribut");
        var grain = InferGrain(lower);
        var multiSeries = !string.IsNullOrWhiteSpace(groupBy);

        return new AnomalyDetectionRequest(
            OriginalUserText: text,
            MetricName: metric,
            GroupBy: groupBy,
            DateColumnHint: "Period",
            ValueColumnHint: "Value",
            LookbackPeriods: Math.Clamp(lookback, 6, 120),
            ForecastPeriods: Math.Clamp(forecastPeriods, 1, 24),
            TopN: Math.Clamp(topN, 3, 20),
            Sensitivity: sensitivity,
            DetectChangePoints: detectChangePoints,
            CompareToForecast: compareForecast,
            RunDriverAnalysis: driver,
            TimeGrain: grain,
            MultiSeriesPreferred: multiSeries);
    }

    private static string InferMetric(string lower)
    {
        if (lower.Contains("revenue")) return "Revenue";
        if (lower.Contains("sales")) return "Sales";
        if (lower.Contains("profit")) return "Profit";
        if (lower.Contains("margin")) return "Margin";
        if (lower.Contains("order")) return "Orders";
        if (lower.Contains("cost")) return "Cost";
        if (lower.Contains("headcount") || lower.Contains("employee")) return "Headcount";
        return "Value";
    }

    private static string? InferGroupBy(string lower)
    {
        string[] dims = ["region", "territory", "country", "branch", "store", "category", "product", "channel", "department", "team", "salesperson", "customer"];
        foreach (var dim in dims)
        {
            if (lower.Contains($"by {dim}") || lower.Contains($"per {dim}") || lower.Contains(dim + " wise") || lower.Contains(dim + "-wise"))
                return Culture(dim);
        }
        return null;
    }

    private static string InferGrain(string lower)
    {
        if (lower.Contains("daily") || lower.Contains("day")) return "day";
        if (lower.Contains("weekly") || lower.Contains("week")) return "week";
        if (lower.Contains("quarter") || lower.Contains("quarterly")) return "quarter";
        return "month";
    }

    private static double InferSensitivity(string lower)
    {
        if (lower.Contains("very sensitive") || lower.Contains("high sensitivity")) return 1.5;
        if (lower.Contains("strict") || lower.Contains("only major")) return 2.75;
        return 2.0;
    }

    private static int ExtractTopN(string lower)
    {
        var m = Regex.Match(lower, @"top\s+(\d{1,2})", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 8;
    }

    private static int ExtractNumberNear(string lower, string anchor, int defaultValue)
    {
        var m = Regex.Match(lower, $@"{Regex.Escape(anchor)}\s+(\d{{1,3}})", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : defaultValue;
    }

    private static string Culture(string value) => char.ToUpperInvariant(value[0]) + value[1..];
}
