using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly.Detections;

public static class BaselineEstimatorSelector
{
    public static IBaselineEstimator Select(AnomalySeries series, SeriesProfile profile, string timeGrain)
    {
        if (profile.HasLikelySeasonality && (timeGrain.Equals("day", StringComparison.OrdinalIgnoreCase) || timeGrain.Equals("week", StringComparison.OrdinalIgnoreCase)))
            return new SeasonalBaselineEstimator(timeGrain.Equals("day", StringComparison.OrdinalIgnoreCase) ? 7 : 4);

        if (profile.HasLikelySeasonality && timeGrain.Equals("month", StringComparison.OrdinalIgnoreCase))
            return new SeasonalBaselineEstimator(12);

        return new RollingBaselineEstimator(4);
    }
}
