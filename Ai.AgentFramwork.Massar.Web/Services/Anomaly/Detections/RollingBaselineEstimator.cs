using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Detections;

public sealed class RollingBaselineEstimator : IBaselineEstimator
{
    private readonly int _window;

    public RollingBaselineEstimator(int window = 4)
    {
        _window = Math.Max(2, window);
    }

    public IReadOnlyList<double> Estimate(AnomalySeries series)
    {
        var result = new List<double>(series.Points.Count);
        for (int i = 0; i < series.Points.Count; i++)
        {
            var start = Math.Max(0, i - _window);
            var prior = series.Points.Skip(start).Take(i - start).Select(p => p.Value).ToList();
            var baseline = prior.Count == 0 ? series.Points[i].Value : prior.Average();
            result.Add(baseline);
        }
        return result;
    }
}
