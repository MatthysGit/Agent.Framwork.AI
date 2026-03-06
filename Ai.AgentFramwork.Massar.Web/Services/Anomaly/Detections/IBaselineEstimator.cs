using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Detections;

public interface IBaselineEstimator
{
    IReadOnlyList<double> Estimate(AnomalySeries series);
}
