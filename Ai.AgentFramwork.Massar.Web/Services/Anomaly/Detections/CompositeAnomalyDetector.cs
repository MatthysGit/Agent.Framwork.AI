using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Detections;

public sealed class CompositeAnomalyDetector
{
    private readonly TimeSeriesAnomalyDetector _timeSeries = new();

    public IReadOnlyList<DetectedAnomaly> Detect(
        AnomalySeries series,
        IReadOnlyList<double> baseline,
        double sensitivity,
        bool compareToForecast,
        int forecastPeriods)
    {
        var all = new List<DetectedAnomaly>();
        all.AddRange(_timeSeries.Detect(series, baseline, sensitivity));
        if (compareToForecast)
            all.AddRange(ForecastComparisonDetector.DetectAgainstForecast(series, forecastPeriods, Math.Max(1.0, sensitivity - 0.5)));

        return all
            .OrderByDescending(a => a.SeverityScore)
            .ThenByDescending(a => Math.Abs(a.DeviationPercent))
            .DistinctBy(a => new { a.Series, a.Period, a.Kind })
            .ToList();
    }
}
