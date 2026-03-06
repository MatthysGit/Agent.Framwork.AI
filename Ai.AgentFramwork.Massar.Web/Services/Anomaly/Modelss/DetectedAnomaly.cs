namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

public sealed record DetectedAnomaly(
    DateTime Period,
    string Series,
    double Actual,
    double Expected,
    double? LowerBound,
    double? UpperBound,
    double Deviation,
    double DeviationPercent,
    double SeverityScore,
    string Kind,
    string Explanation);
