namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

public sealed record AnomalyDetectionRequest(
    string OriginalUserText,
    string MetricName,
    string? GroupBy,
    string? DateColumnHint,
    string? ValueColumnHint,
    int LookbackPeriods,
    int ForecastPeriods,
    int TopN,
    double Sensitivity,
    bool DetectChangePoints,
    bool CompareToForecast,
    bool RunDriverAnalysis,
    string TimeGrain,
    bool MultiSeriesPreferred);
