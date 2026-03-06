using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly;


public static class AnomalyChartBuilder
{
    public static (List<string> Labels, List<string> SeriesNames, List<double[]> SeriesValues, bool MultiSeries) Build(AnomalyDataset dataset, Dictionary<string, IReadOnlyList<double>> baselines)
    {
        var labels = dataset.Series.SelectMany(s => s.Points.Select(p => p.Period)).Distinct().OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd")).ToList();

        if (dataset.IsMultiSeries)
        {
            var seriesNames = new List<string>();
            var values = new List<double[]>();
            foreach (var s in dataset.Series.Take(6))
            {
                var map = s.Points.ToDictionary(p => p.Period.ToString("yyyy-MM-dd"), p => p.Value);
                var row = labels.Select(l => map.TryGetValue(l, out var v) ? v : 0d).ToArray();
                seriesNames.Add(s.Name);
                values.Add(row);
            }
            return (labels, seriesNames, values, true);
        }
        else
        {
            var s = dataset.Series[0];
            var actual = s.Points.Select(p => p.Value).ToArray();
            var expected = baselines.TryGetValue(s.Name, out var baseline)
                ? baseline.ToArray()
                : actual;
            return (s.Points.Select(p => p.Period.ToString("yyyy-MM-dd")).ToList(), new List<string> { "Actual", "Expected" }, new List<double[]> { actual, expected }, true);
        }
    }
}

