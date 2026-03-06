using System.Text;
using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly;

public static class AnomalyNarrativeBuilder
{
    public static string Build(AnomalyDetectionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Anomaly Detection Summary");
        sb.AppendLine(result.Summary);
        sb.AppendLine();

        if (result.Anomalies.Count > 0)
        {
            sb.AppendLine("Key Findings");
            foreach (var item in result.Anomalies.Take(8))
                sb.AppendLine($"• {item.Series} on {item.Period:yyyy-MM-dd}: {item.Kind} of {item.Deviation:0.##} ({item.DeviationPercent:0.##}% vs expected {item.Expected:0.##}).");
            sb.AppendLine();
        }

        if (result.ChangePoints.Count > 0)
        {
            sb.AppendLine("Structural Changes");
            foreach (var cp in result.ChangePoints.Take(5))
                sb.AppendLine($"• {cp.Series} shows a structural shift around {cp.Period:yyyy-MM-dd}; average changed by {cp.ShiftPercent:0.##}%.");
            sb.AppendLine();
        }

        if (result.DriverAnalysis?.TopDrivers.Count > 0)
        {
            sb.AppendLine("Top Drivers");
            foreach (var d in result.DriverAnalysis.TopDrivers.Take(5))
                sb.AppendLine($"• {d.Member}: contributed {d.ContributionPercent:0.##}% of total anomaly magnitude.");
            sb.AppendLine();
        }

        if (result.Warnings.Count > 0)
        {
            sb.AppendLine("Warnings");
            foreach (var warning in result.Warnings)
                sb.AppendLine($"• {warning}");
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }
}
