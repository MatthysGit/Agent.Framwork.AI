namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

public sealed class AnomalyInputPoint
{
    public DateTime Period { get; init; }
    public double Value { get; init; }
    public string? Series { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
