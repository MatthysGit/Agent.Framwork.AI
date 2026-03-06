namespace Ai.AgentFramwork.Massar.Web.Services.WhatIfs.Modelss;

public sealed record WhatIfSimulationPoint(
    string Label,
    double BaselineValue,
    double ScenarioValue,
    double DeltaValue,
    double DeltaPct,
    string? Series = null,
    DateTime? SortDate = null);
