using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Detections;

public interface IAnomalyDetector
{
    IReadOnlyList<DetectedAnomaly> Detect(AnomalySeries series, IReadOnlyList<double> baseline, double sensitivity);
}
