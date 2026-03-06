using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly;

public static class AnomalyTableBuilder
{
    public static (List<string> Columns, List<Dictionary<string, string>> Rows) Build(AnomalyDetectionResult result, int topN)
    {
        var columns = new List<string> { "Series", "Period", "Kind", "Actual", "Expected", "Deviation", "Deviation %", "Severity" };
        var rows = result.Anomalies
            .OrderByDescending(a => a.SeverityScore)
            .Take(topN)
            .Select(a => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Series"] = a.Series,
                ["Period"] = a.Period.ToString("yyyy-MM-dd"),
                ["Kind"] = a.Kind,
                ["Actual"] = a.Actual.ToString("0.##"),
                ["Expected"] = a.Expected.ToString("0.##"),
                ["Deviation"] = a.Deviation.ToString("0.##"),
                ["Deviation %"] = a.DeviationPercent.ToString("0.##"),
                ["Severity"] = a.SeverityScore.ToString("0.##")
            })
            .ToList();

        return (columns, rows);
    }
}
