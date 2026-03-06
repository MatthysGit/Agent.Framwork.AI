using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Detections;

public static class SeriesProfiler
{
    public static SeriesProfile Build(AnomalySeries series)
    {
        var values = series.Points.Select(p => p.Value).ToList();
        if (values.Count == 0)
            return new SeriesProfile();

        var mean = values.Average();
        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
        var std = Math.Sqrt(variance);
        var firstHalf = values.Take(values.Count / 2).DefaultIfEmpty(0d).Average();
        var secondHalf = values.Skip(values.Count / 2).DefaultIfEmpty(0d).Average();

        return new SeriesProfile
        {
            PointCount = values.Count,
            Mean = mean,
            StandardDeviation = std,
            HasLikelySeasonality = values.Count >= 12,
            HasSufficientHistory = values.Count >= 8,
            HasTrend = Math.Abs(secondHalf - firstHalf) > (std * 0.5)
        };
    }
}
