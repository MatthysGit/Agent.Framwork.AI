namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

public sealed class DriverAnalysisResult
{
    public string PrimaryDimension { get; init; } = string.Empty;
    public List<DriverContributionItem> TopDrivers { get; init; } = new();
    public double ExplainedShare { get; init; }
    public string Notes { get; init; } = string.Empty;
}
