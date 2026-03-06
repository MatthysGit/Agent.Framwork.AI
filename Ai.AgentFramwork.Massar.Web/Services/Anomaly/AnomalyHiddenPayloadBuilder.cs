using System.Text.Json;
using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly;

public static class AnomalyHiddenPayloadBuilder
{
    public static string Build(AnomalyDetectionResult result)
    {
        return JsonSerializer.Serialize(new
        {
            type = "anomaly_result",
            title = result.Title,
            summary = result.Summary,
            anomalies = result.Anomalies.Select(a => new
            {
                period = a.Period,
                series = a.Series,
                actual = a.Actual,
                expected = a.Expected,
                deviation = a.Deviation,
                deviationPercent = a.DeviationPercent,
                severity = a.SeverityScore,
                kind = a.Kind,
                explanation = a.Explanation
            }).ToList(),
            changePoints = result.ChangePoints.Select(c => new
            {
                period = c.Period,
                series = c.Series,
                preAverage = c.PreAverage,
                postAverage = c.PostAverage,
                shiftPercent = c.ShiftPercent,
                confidence = c.Confidence
            }).ToList(),
            drivers = result.DriverAnalysis?.TopDrivers.Select(d => new
            {
                dimension = d.Dimension,
                member = d.Member,
                contribution = d.Contribution,
                contributionPercent = d.ContributionPercent,
                rank = d.Rank
            }).ToList(),
            warnings = result.Warnings
        });
    }
}
