using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Detections;

public static class AnomalyDriverAnalyzer
{
    public static DriverAnalysisResult? Analyze(AnomalyDataset dataset, IReadOnlyList<DetectedAnomaly> anomalies, int topN)
    {
        if (!dataset.IsMultiSeries || anomalies.Count == 0)
            return null;

        var grouped = anomalies
            .GroupBy(a => a.Series, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Series = g.Key,
                Contribution = g.Sum(x => Math.Abs(x.Deviation)),
                AvgExpected = g.Average(x => x.Expected),
                AvgActual = g.Average(x => x.Actual)
            })
            .OrderByDescending(x => x.Contribution)
            .Take(topN)
            .ToList();

        var total = grouped.Sum(x => x.Contribution);
        if (total <= 0)
            return null;

        return new DriverAnalysisResult
        {
            PrimaryDimension = dataset.GroupBy ?? "Series",
            ExplainedShare = 100,
            Notes = "Driver analysis ranked the largest anomalous contributors by absolute deviation.",
            TopDrivers = grouped.Select((x, i) => new DriverContributionItem(
                Dimension: dataset.GroupBy ?? "Series",
                Member: x.Series,
                Actual: x.AvgActual,
                Expected: x.AvgExpected,
                Contribution: x.Contribution,
                ContributionPercent: (x.Contribution / total) * 100d,
                Rank: i + 1)).ToList()
        };
    }
}
