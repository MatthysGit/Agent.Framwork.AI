using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Detections;

public sealed class TimeSeriesAnomalyDetector : IAnomalyDetector
{
    public IReadOnlyList<DetectedAnomaly> Detect(AnomalySeries series, IReadOnlyList<double> baseline, double sensitivity)
    {
        var residuals = new List<double>();
        for (int i = 0; i < series.Points.Count; i++)
            residuals.Add(series.Points[i].Value - baseline[i]);

        var residualStd = StdDev(residuals);
        var threshold = Math.Max(1e-9, residualStd * sensitivity);
        var results = new List<DetectedAnomaly>();

        for (int i = 0; i < series.Points.Count; i++)
        {
            var actual = series.Points[i].Value;
            var expected = baseline[i];
            var deviation = actual - expected;
            if (Math.Abs(deviation) < threshold)
                continue;

            var pct = Math.Abs(expected) < 1e-9 ? 0 : (deviation / expected) * 100d;
            var severity = Math.Abs(deviation) / Math.Max(1e-9, residualStd);
            var band = residualStd * sensitivity;
            var kind = deviation > 0 ? "Spike" : "Drop";
            results.Add(new DetectedAnomaly(
                Period: series.Points[i].Period,
                Series: series.Name,
                Actual: actual,
                Expected: expected,
                LowerBound: expected - band,
                UpperBound: expected + band,
                Deviation: deviation,
                DeviationPercent: pct,
                SeverityScore: severity,
                Kind: kind,
                Explanation: $"{kind} detected versus baseline ({expected:0.##})."));
        }

        return results;
    }

    private static double StdDev(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var mean = values.Average();
        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
        return Math.Sqrt(variance);
    }
}
