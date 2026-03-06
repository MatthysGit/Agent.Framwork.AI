namespace Ai.AgentFramwork.Massar.Web.Services.WhatIfs.Modelss;

public sealed record WhatIfInputPoint(
    string Label,
    double Value,
    string? Series = null,
    DateTime? SortDate = null);
