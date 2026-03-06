using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Detections;

public static class ChangePointDetector
{
    public static IReadOnlyList<ChangePointResult> Detect(AnomalySeries series)
    {
        var results = new List<ChangePointResult>();
        if (series.Points.Count < 10)
            return results;

        var best = default(ChangePointResult);
        var bestScore = 0d;

        for (int split = 4; split < series.Points.Count - 4; split++)
        {
            var pre = series.Points.Take(split).Select(p => p.Value).ToList();
            var post = series.Points.Skip(split).Select(p => p.Value).ToList();
            var preAvg = pre.Average();
            var postAvg = post.Average();
            var diff = Math.Abs(postAvg - preAvg);
            var pooled = Math.Max(1e-9, StdDev(pre.Concat(post).ToList()));
            var score = diff / pooled;
            if (score > bestScore && score >= 1.75)
            {
                bestScore = score;
                var shiftPct = Math.Abs(preAvg) < 1e-9 ? 0 : ((postAvg - preAvg) / preAvg) * 100d;
                best = new ChangePointResult(
                    Period: series.Points[split].Period,
                    Series: series.Name,
                    PreAverage: preAvg,
                    PostAverage: postAvg,
                    ShiftPercent: shiftPct,
                    Confidence: Math.Min(0.99, score / 4d),
                    Explanation: "Potential structural break detected between pre and post windows.");
            }
        }

        if (best is not null)
            results.Add(best);

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
