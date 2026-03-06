namespace Ai.AgentFramwork.Massar.Web.Services.Forecasting;


public enum ForecastGrain
{
    Day,
    Week,
    Month,
    Quarter,
    Year
}

public sealed class ForecastRequest
{
    public string Metric { get; set; } = "Value";
    public ForecastGrain Grain { get; set; } = ForecastGrain.Month;
    public int Horizon { get; set; } = 6;
    public int HistoryPeriods { get; set; } = 24;
    public string? GroupBy { get; set; }
    public bool IncludeScenarios { get; set; } = true;
}
