namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

public sealed class AnomalySeries
{
    public string Name { get; init; } = "Overall";
    public IReadOnlyList<AnomalyInputPoint> Points { get; init; } = Array.Empty<AnomalyInputPoint>();
}
