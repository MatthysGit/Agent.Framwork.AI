using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;
using Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;
using Ai.AgentFramwork.Massar.Web.Services.Forecasting;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Detections;

public static class ForecastComparisonDetector
{
    public static IReadOnlyList<DetectedAnomaly> DetectAgainstForecast(AnomalySeries series, int forecastPeriods, double sensitivity)
    {
        var results = new List<DetectedAnomaly>();
        if (series.Points.Count < 8)
            return results;

        var request = new ForecastRequest
        {
            Metric = series.Name,
            Grain = ForecastGrain.Month,
            Horizon = Math.Max(1, Math.Min(6, forecastPeriods)),
            HistoryPeriods = Math.Max(8, series.Points.Count),
            GroupBy = null,
            IncludeScenarios = false
        };

        var input = series.Points.Select(p => new ForecastInputPoint(p.Period, p.Value, null)).ToList();
        var forecast = ForecastingService.GenerateForecast(request, input);
        var projected = forecast.Points.Where(p => p.IsForecast && string.Equals(p.Scenario, "Baseline", StringComparison.OrdinalIgnoreCase)).ToList();
        if (projected.Count == 0)
            return results;

        var lastActual = series.Points[^1];
        var firstForecast = projected[0];
        var expected = firstForecast.Value;
        var width = Math.Max(1e-9, (firstForecast.UpperBound ?? expected) - expected);
        var deviation = lastActual.Value - expected;
        if (Math.Abs(deviation) >= width * sensitivity)
        {
            var pct = Math.Abs(expected) < 1e-9 ? 0 : (deviation / expected) * 100d;
            results.Add(new DetectedAnomaly(
                Period: lastActual.Period,
                Series: series.Name,
                Actual: lastActual.Value,
                Expected: expected,
                LowerBound: firstForecast.LowerBound,
                UpperBound: firstForecast.UpperBound,
                Deviation: deviation,
                DeviationPercent: pct,
                SeverityScore: Math.Abs(deviation) / width,
                Kind: deviation > 0 ? "Above forecast" : "Below forecast",
                Explanation: "Latest actual value deviates materially from forecast baseline."));
        }

        return results;
    }
}
