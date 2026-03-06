using System.Text.Json.Serialization;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

public sealed class AnomalyDetectionResult
{
    public string Title { get; set; } = "Anomaly Detection";
    public string Summary { get; set; } = string.Empty;
    public string Narrative { get; set; } = string.Empty;
    public List<DetectedAnomaly> Anomalies { get; set; } = new();
    public List<ChangePointResult> ChangePoints { get; set; } = new();
    public DriverAnalysisResult? DriverAnalysis { get; set; }
    public List<string> Warnings { get; set; } = new();
    public string ChartTitle { get; set; } = "Anomaly Detection";
    public bool MultiSeries { get; set; }
    public List<string> ChartLabels { get; set; } = new();
    public List<string> SeriesNames { get; set; } = new();
    public List<double[]> SeriesValues { get; set; } = new();
    public List<string> TableColumns { get; set; } = new();
    public List<Dictionary<string, string>> TableRows { get; set; } = new();

    [JsonIgnore]
    public bool HasChart => ChartLabels.Count > 0 && SeriesValues.Count > 0;
}
