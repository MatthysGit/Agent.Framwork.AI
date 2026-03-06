using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Detections;
using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;


namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly;

public sealed class AnomalyDetectionCoordinator
{
    public AnomalyDetectionResult Execute(AnomalyDetectionRequest request, AnomalyDataset dataset)
    {
        var warnings = AnomalyDataValidator.Validate(dataset);
        var baselines = new Dictionary<string, IReadOnlyList<double>>(StringComparer.OrdinalIgnoreCase);
        var detector = new CompositeAnomalyDetector();
        var anomalies = new List<DetectedAnomaly>();
        var changePoints = new List<ChangePointResult>();

        foreach (var series in dataset.Series)
        {
            var profile = SeriesProfiler.Build(series);
            var estimator = BaselineEstimatorSelector.Select(series, profile, request.TimeGrain);
            var baseline = estimator.Estimate(series);
            baselines[series.Name] = baseline;

            anomalies.AddRange(detector.Detect(series, baseline, request.Sensitivity, request.CompareToForecast, request.ForecastPeriods));
            if (request.DetectChangePoints)
                changePoints.AddRange(ChangePointDetector.Detect(series));
        }

        anomalies = anomalies
            .OrderByDescending(a => a.SeverityScore)
            .ThenByDescending(a => Math.Abs(a.DeviationPercent))
            .Take(request.TopN)
            .ToList();

        var driver = request.RunDriverAnalysis ? AnomalyDriverAnalyzer.Analyze(dataset, anomalies, request.TopN) : null;

        var summary = BuildSummary(dataset, anomalies, changePoints, warnings);
        var chart = AnomalyChartBuilder.Build(dataset, baselines);

        var result = new AnomalyDetectionResult
        {
            Title = $"Anomaly Detection - {request.MetricName}",
            Summary = summary,
            MultiSeries = chart.MultiSeries,
            ChartTitle = $"{request.MetricName} anomaly view",
            ChartLabels = chart.Labels,
            SeriesNames = chart.SeriesNames,
            SeriesValues = chart.SeriesValues,
            Anomalies = anomalies,
            ChangePoints = changePoints,
            DriverAnalysis = driver,
            Warnings = warnings
        };

        var table = AnomalyTableBuilder.Build(result, request.TopN);
        result.Narrative = AnomalyNarrativeBuilder.Build(result);
        result.TableColumns = table.Columns;
        result.TableRows = table.Rows;
        return result;
    }

    private static string BuildSummary(AnomalyDataset dataset, IReadOnlyList<DetectedAnomaly> anomalies, IReadOnlyList<ChangePointResult> changePoints, IReadOnlyList<string> warnings)
    {
        if (dataset.Series.Count == 0)
            return "No usable time series was returned for anomaly detection.";

        if (anomalies.Count == 0 && changePoints.Count == 0)
            return warnings.Count == 0
                ? "No material anomalies were detected in the returned dataset."
                : "No material anomalies were detected, but data quality warnings may limit confidence.";

        var top = anomalies.FirstOrDefault();
        if (top is null && changePoints.Count > 0)
        {
            var cp = changePoints[0];
            return $"A likely structural change was detected for {cp.Series} around {cp.Period:yyyy-MM-dd}, with an average shift of {cp.ShiftPercent:0.##}%.";
        }

        return $"Detected {anomalies.Count} material anomaly{(anomalies.Count == 1 ? string.Empty : "ies")} across {dataset.Series.Count} series. The largest signal was {top!.Kind.ToLowerInvariant()} in {top.Series} on {top.Period:yyyy-MM-dd} ({top.DeviationPercent:0.##}% vs expected).";
    }
}
