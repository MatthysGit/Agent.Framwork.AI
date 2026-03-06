using Ai.AgentFramwork.Massar.Web.Services.Forecasting;
using System.Text.RegularExpressions;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;

public static class ForecastingService
{
    public static ForecastRequest ParseRequest(string userText)
    {
        var t = (userText ?? string.Empty).ToLowerInvariant();
        var req = new ForecastRequest();

        req.Grain = t.Contains("quarter") ? ForecastGrain.Quarter
            : t.Contains("year") ? ForecastGrain.Year
            : t.Contains("week") ? ForecastGrain.Week
            : t.Contains("day") ? ForecastGrain.Day
            : ForecastGrain.Month;

        var m = Regex.Match(t, @"next\s+(\d{1,2})");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var h))
            req.Horizon = Math.Max(1, Math.Min(24, h));
        else if (req.Grain == ForecastGrain.Quarter)
            req.Horizon = 4;
        else if (req.Grain == ForecastGrain.Year)
            req.Horizon = 3;

        if (t.Contains("by "))
        {
            var by = Regex.Match(userText, @"\bby\s+([A-Za-z0-9_ ]{2,40})", RegexOptions.IgnoreCase);
            if (by.Success)
                req.GroupBy = by.Groups[1].Value.Trim().TrimEnd('?', '.', ',');
        }

        if (t.Contains("revenue")) req.Metric = "Revenue";
        else if (t.Contains("sales")) req.Metric = "Sales";
        else if (t.Contains("order")) req.Metric = "Orders";
        else if (t.Contains("demand")) req.Metric = "Demand";
        else if (t.Contains("margin")) req.Metric = "Margin";

        req.HistoryPeriods = req.Grain switch
        {
            ForecastGrain.Day => 90,
            ForecastGrain.Week => 52,
            ForecastGrain.Quarter => 12,
            ForecastGrain.Year => 6,
            _ => 24
        };

        return req;
    }

    public static ForecastResult GenerateForecast(ForecastRequest request, List<ForecastInputPoint> input)
    {
        var result = new ForecastResult
        {
            Title = $"{request.Metric} Forecast",
            Grouped = input.Any(p => !string.IsNullOrWhiteSpace(p.Series))
        };

        var groups = input
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Series) ? "Overall" : p.Series!)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(x => x.Period).ToList();
            foreach (var point in ordered)
            {
                result.Points.Add(new ForecastPoint
                {
                    Period = point.Period,
                    Series = group.Key,
                    Value = point.Value,
                    IsForecast = false,
                    Scenario = ForecastingScenario.Baseline
                });
            }

            if (ordered.Count < 3)
                continue;

            var baseline = ProjectSeries(request, ordered, 1.00, ForecastingScenario.Baseline);
            var optimistic = request.IncludeScenarios ? ProjectSeries(request, ordered, 1.08, ForecastingScenario.Optimistic) : new List<ForecastPoint>();
            var conservative = request.IncludeScenarios ? ProjectSeries(request, ordered, 0.92, ForecastingScenario.Conservative) : new List<ForecastPoint>();

            result.Points.AddRange(baseline);
            result.Points.AddRange(optimistic);
            result.Points.AddRange(conservative);
        }

        var historyCount = result.Points.Count(p => !p.IsForecast);
        var forecastCount = result.Points.Count(p => p.IsForecast && p.Scenario == ForecastingScenario.Baseline);

        var latestHistory = result.Points
            .Where(p => !p.IsForecast)
            .GroupBy(p => p.Series ?? "Overall")
            .Select(g => g.OrderByDescending(x => x.Period).First())
            .ToList();

        var latestForecast = result.Points
            .Where(p => p.IsForecast && p.Scenario == ForecastingScenario.Baseline)
            .GroupBy(p => p.Series ?? "Overall")
            .Select(g => g.OrderByDescending(x => x.Period).First())
            .ToList();

        var histAvg = latestHistory.Count == 0 ? 0 : latestHistory.Average(x => x.Value);
        var fcAvg = latestForecast.Count == 0 ? 0 : latestForecast.Average(x => x.Value);
        var delta = histAvg == 0 ? 0 : ((fcAvg - histAvg) / histAvg) * 100.0;

        result.Summary = result.Grouped
            ? $"Generated a grouped {request.Grain.ToString().ToLowerInvariant()} forecast across {groups.Count} series. Baseline forecast averages are {(delta >= 0 ? "above" : "below")} the latest historical level by {Math.Abs(delta):0.0}%."
            : $"Generated a {request.Grain.ToString().ToLowerInvariant()} forecast for the next {request.Horizon} periods. Baseline forecast is {(delta >= 0 ? "above" : "below")} the latest historical level by {Math.Abs(delta):0.0}%.";

        result.Assumptions.Add($"Forecast is based on {historyCount} historical points returned by the data source.");
        result.Assumptions.Add("Baseline uses a simple trend with lightweight seasonality when enough history exists.");
        result.Assumptions.Add("Optimistic and conservative scenarios apply modest uplifts and downticks to the baseline.");

        return result;
    }

    private static List<ForecastPoint> ProjectSeries(ForecastRequest request, List<ForecastInputPoint> ordered, double scenarioMultiplier, string scenario)
    {
        var values = ordered.Select(x => x.Value).ToList();
        var n = values.Count;
        var slope = n <= 1 ? 0 : (values[^1] - values[0]) / Math.Max(1, n - 1);
        var last = ordered[^1];
        var seasonalWindow = request.Grain == ForecastGrain.Month && n >= 12 ? 12
            : request.Grain == ForecastGrain.Quarter && n >= 4 ? 4
            : 0;

        var result = new List<ForecastPoint>();

        for (var i = 1; i <= request.Horizon; i++)
        {
            var trendBase = last.Value + (slope * i);
            double seasonalAdj = 0;

            if (seasonalWindow > 0 && n >= seasonalWindow)
            {
                var idx = (n - seasonalWindow + ((i - 1) % seasonalWindow));
                idx = Math.Max(0, Math.Min(values.Count - 1, idx));
                seasonalAdj = (values[idx] - values.Skip(Math.Max(0, n - seasonalWindow)).Average()) * 0.35;
            }

            var baseline = Math.Max(0, (trendBase + seasonalAdj) * scenarioMultiplier);
            var period = AddPeriod(last.Period, request.Grain, i);
            var band = Math.Max(1, baseline * 0.10);

            result.Add(new ForecastPoint
            {
                Period = period,
                Series = last.Series ?? "Overall",
                Value = baseline,
                LowerBound = Math.Max(0, baseline - band),
                UpperBound = baseline + band,
                IsForecast = true,
                Scenario = scenario
            });
        }

        return result;
    }

    private static DateTime AddPeriod(DateTime dt, ForecastGrain grain, int offset)
    {
        return grain switch
        {
            ForecastGrain.Day => dt.AddDays(offset),
            ForecastGrain.Week => dt.AddDays(7 * offset),
            ForecastGrain.Quarter => dt.AddMonths(3 * offset),
            ForecastGrain.Year => dt.AddYears(offset),
            _ => dt.AddMonths(offset)
        };
    }
}
