namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

public sealed record ChangePointResult(
    DateTime Period,
    string Series,
    double PreAverage,
    double PostAverage,
    double ShiftPercent,
    double Confidence,
    string Explanation);
