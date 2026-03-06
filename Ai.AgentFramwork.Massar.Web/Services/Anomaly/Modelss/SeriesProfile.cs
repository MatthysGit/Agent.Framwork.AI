namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

public sealed class SeriesProfile
{
    public int PointCount { get; init; }
    public double Mean { get; init; }
    public double StandardDeviation { get; init; }
    public bool HasLikelySeasonality { get; init; }
    public bool HasSufficientHistory { get; init; }
    public bool HasTrend { get; init; }
}
