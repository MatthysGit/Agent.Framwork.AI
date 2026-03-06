namespace Ai.AgentFramwork.Massar.Web.Services.WhatIfs.Modelss;

public sealed class WhatIfSimulationResult
{
    public string Title { get; init; } = "What-If Simulation";
    public string Summary { get; init; } = string.Empty;
    public string MetricLabel { get; init; } = "Value";
    public string ScenarioLabel { get; init; } = string.Empty;
    public string VariableName { get; init; } = string.Empty;
    public double ChangePercent { get; init; }
    public bool Grouped { get; init; }
    public bool TimeSeriesLike { get; init; }
    public List<string> Assumptions { get; init; } = new();
    public List<WhatIfSimulationPoint> Points { get; init; } = new();
}
