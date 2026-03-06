

using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Detections;

public static class AnomalyDataValidator
{
    public static List<string> Validate(AnomalyDataset dataset)
    {
        var warnings = new List<string>();
        if (dataset.Series.Count == 0)
        {
            warnings.Add("No series were returned for anomaly detection.");
            return warnings;
        }

        foreach (var series in dataset.Series)
        {
            if (series.Points.Count < 6)
                warnings.Add($"Series '{series.Name}' has fewer than 6 data points; anomaly confidence is limited.");

            var std = StdDev(series.Points.Select(p => p.Value).ToList());
            if (std == 0)
                warnings.Add($"Series '{series.Name}' has no variance; anomalies may not be detectable.");
        }

        return warnings;
    }

    private static double StdDev(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var mean = values.Average();
        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
        return Math.Sqrt(variance);
    }
}
