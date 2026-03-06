namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

public sealed class AnomalyDataset
{
    public IReadOnlyList<AnomalySeries> Series { get; init; } = Array.Empty<AnomalySeries>();
    public bool IsMultiSeries { get; init; }
    public string MetricLabel { get; init; } = "Value";
    public string? GroupBy { get; init; }
}
