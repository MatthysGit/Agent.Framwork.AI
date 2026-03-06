namespace Ai.AgentFramwork.Massar.Web.Services.Forecasting;

public sealed record ForecastInputPoint(DateTime Period, double Value, string? Series);

public sealed class ForecastPoint
{
    public DateTime Period { get; set; }
    public string? Series { get; set; }
    public double Value { get; set; }
    public double? LowerBound { get; set; }
    public double? UpperBound { get; set; }
    public bool IsForecast { get; set; }
    public string? Scenario { get; set; }
}

public sealed class ForecastResult
{
    public string Title { get; set; } = "Forecast";
    public string Summary { get; set; } = "";
    public bool Grouped { get; set; }
    public List<ForecastPoint> Points { get; set; } = new();
    public List<string> Assumptions { get; set; } = new();
}
