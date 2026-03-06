using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Detections;

public sealed class SeasonalBaselineEstimator : IBaselineEstimator
{
    private readonly int _seasonLength;

    public SeasonalBaselineEstimator(int seasonLength = 7)
    {
        _seasonLength = Math.Max(2, seasonLength);
    }

    public IReadOnlyList<double> Estimate(AnomalySeries series)
    {
        var result = new List<double>(series.Points.Count);
        for (int i = 0; i < series.Points.Count; i++)
        {
            var comparable = new List<double>();
            for (int j = i - _seasonLength; j >= 0; j -= _seasonLength)
                comparable.Add(series.Points[j].Value);
            result.Add(comparable.Count == 0 ? series.Points[i].Value : comparable.Average());
        }
        return result;
    }
}
