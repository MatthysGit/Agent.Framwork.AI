using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Anomaly;

public static class AnomalySupportPromptBuilder
{
    public static string Build(AnomalyDetectionRequest request, bool stricter = false)
    {
        var seriesLine = request.GroupBy is null
            ? "- Return rows with EXACT columns: Period, Value."
            : $"- Return rows with EXACT columns: Period, Value, Series. Series MUST contain the grouping label for '{request.GroupBy}'.";

        var strictLine = stricter
            ? "- You previously asked a question. That is NOT allowed. Choose the most reasonable interpretation and proceed."
            : "- Do NOT ask follow-up questions. Choose the most reasonable interpretation and proceed.";

        return $@"
You are the SQL data exploration agent for anomaly detection support data.

{strictLine}

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

MANDATORY OUTPUT RULES:
- Return ONLY the JSON returned by ExecuteSelectAsync.
- No markdown, no prose, no wrapping.
- Aggregate the data into a time series suitable for anomaly detection.
- Bucket by {request.TimeGrain}.
- Return historical data ordered by Period ascending.
{seriesLine}
- Period MUST be a date-like bucket label or ISO date string.
- Value MUST be numeric.
- Include enough history to support anomaly detection, preferably the last {request.LookbackPeriods} periods.
- Prefer the metric that best matches '{request.MetricName}'.
- If multiple interpretations are possible, choose the business metric most directly aligned to the user's wording.

USER REQUEST:
{request.OriginalUserText}
";
    }
}
