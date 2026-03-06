namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

public sealed record DriverContributionItem(
    string Dimension,
    string Member,
    double Actual,
    double Expected,
    double Contribution,
    double ContributionPercent,
    int Rank);
