using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Services.Chat.DecisionTracking;
using Ai.AgentFramwork.Massar.Web.Services.Forecasting;
using Ai.AgentFramwork.Massar.Web.Tools;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ai.AgentFramwork.Massar.Web.Services.Anomaly;
using Ai.AgentFramwork.Massar.Web.Services.Anomaly.Modelss;
using Ai.AgentFramwork.Massar.Web.Services.WhatIfs;
using Ai.AgentFramwork.Massar.Web.Services.WhatIfs.Modelss;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;

public sealed partial class ChatPipeline
{
    private async Task<PipelineResult> ExecuteDataIntelligenceAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        var supportPrompt = BuildDataIntelligenceSupportDataPrompt(userText);

        await TrackRouteAsync(ChatAgentFactory.DataExplorerAgentName, "entered", ct);
        //await TrackRouteAsync(ChatAgentFactory.DataExplorerAgentName, "entered", ct);
        var support = await _caller.CallAgentAsync(
            ChatAgentFactory.DataExplorerAgentName,
            supportPrompt,
            cancellationToken: ct);

        var supportText = (support.Text ?? string.Empty).Trim();

        if (LooksLikeClarifyingQuestion(supportText))
        {
            Console.WriteLine("[DATA_INTELLIGENCE_RETRY] DataExplorer returned a question. Retrying with stricter enforcement.");

            var retryPrompt = BuildDataIntelligenceSupportDataPrompt(userText, stricter: true);

            await TrackRouteAsync(ChatAgentFactory.DataExplorerAgentName, "entered", ct);
            //await TrackRouteAsync(ChatAgentFactory.DataExplorerAgentName, "executive-insight support-data retry", ct);
            //await TrackRouteAsync(ChatAgentFactory.DataExplorerAgentName, "forecast support-data retry", ct);
            support = await _caller.CallAgentAsync(
                ChatAgentFactory.DataExplorerAgentName,
                retryPrompt,
                cancellationToken: ct);

            supportText = (support.Text ?? string.Empty).Trim();
        }

        var finalSupportText = PrepareDataIntelligenceSupportPayload(userText, supportText);

        var synthesisPrompt = BuildDataIntelligenceSynthesisPrompt(userText, finalSupportText);

        await TrackRouteAsync(ChatAgentFactory.DataIntelligenceAgentName, "entered", ct);
        var intelligence = await _caller.CallAgentAsync(
            ChatAgentFactory.DataIntelligenceAgentName,
            synthesisPrompt,
            cancellationToken: ct);

        var intelligenceText = (intelligence.Text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(intelligenceText))
        {
            intelligenceText =
                "## Data Intelligence Summary\n" +
                "I couldn’t produce a grounded data intelligence analysis from the available data.\n\n" +
                "## What the data suggests\n" +
                "- No reliable support payload was returned from the database.\n\n" +
                "## Key signals / patterns\n" +
                "- There was not enough structured evidence to identify meaningful patterns.\n\n" +
                "## Risks or watchouts\n" +
                "- Decisions based on incomplete data may be misleading.\n\n" +
                "## Recommended actions\n" +
                "- Ask for a more targeted analytical request such as sales by region, revenue by month, margin by category, or orders by status.";
        }

        intelligenceText += WrapHiddenToolPayload(
            "data_intelligence_result",
            JsonSerializer.Serialize(new
            {
                type = "data_intelligence",
                question = userText,
                supportPayload = finalSupportText
            }));

        var checkedAnswer = await RunPpiSafeAsync(userText, intelligenceText, ct);

        await TrackRouteAsync(ChatAgentFactory.DataIntelligenceAgentName, "entered", ct);
        return await MaybeAttachDecisionCandidateAsync(
            userText,
            checkedAnswer,
            ChatAgentFactory.DataIntelligenceAgentName,
            routerReason + " (grounded via DataExplorerAgent)",
            ct);
    }

    private async Task<PipelineResult> ExecuteExecutiveInsightAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        var supportPrompt = BuildExecutiveSupportDataPrompt(userText);

        var support = await _caller.CallAgentAsync(
            ChatAgentFactory.DataExplorerAgentName,
            supportPrompt,
            cancellationToken: ct);

        var supportText = (support.Text ?? string.Empty).Trim();

        if (LooksLikeClarifyingQuestion(supportText))
        {
            Console.WriteLine("[EXECUTIVE_RETRY] DataExplorer returned a question. Retrying with stricter enforcement.");

            var retryPrompt = BuildExecutiveSupportDataPrompt(userText, stricter: true);

            support = await _caller.CallAgentAsync(
                ChatAgentFactory.DataExplorerAgentName,
                retryPrompt,
                cancellationToken: ct);

            supportText = (support.Text ?? string.Empty).Trim();
        }

        var finalSupportText = PrepareExecutiveSupportPayload(userText, supportText);

        var executivePrompt = BuildExecutiveSynthesisPrompt(userText, finalSupportText);

        await TrackRouteAsync(ChatAgentFactory.ExecutiveInsightAgentName, "entered", ct);
        var executive = await _caller.CallAgentAsync(
            ChatAgentFactory.ExecutiveInsightAgentName,
            executivePrompt,
            cancellationToken: ct);

        var executiveText = (executive.Text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(executiveText))
        {
            executiveText = "Executive Summary\nI couldn’t generate a grounded executive summary from the available data.\n\nKey Insights\n• No supporting data was returned from the database.\n\nRisks\n• Leadership decisions may be delayed without supporting metrics.\n\nOpportunities\n• Run a more specific business-performance query to gather supporting facts.\n\nRecommended Actions\n• Ask for a focused executive summary such as sales by territory, revenue trend, or headcount by department.";
        }

        var checkedAnswer = await RunPpiSafeAsync(userText, executiveText, ct);

        await TrackRouteAsync(ChatAgentFactory.ExecutiveInsightAgentName, "entered", ct);
        return await MaybeAttachDecisionCandidateAsync(
            userText,
            checkedAnswer,
            ChatAgentFactory.ExecutiveInsightAgentName,
            routerReason + " (grounded via DataExplorerAgent)",
            ct);
    }


    private async Task<PipelineResult> ExecuteForecastAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        var request = ForecastingService.ParseRequest(userText);

        var historicalPrompt = BuildForecastSupportDataPrompt(userText, request);

        await TrackRouteAsync(ChatAgentFactory.DataExplorerAgentName, "entered", ct);
        var support = await _caller.CallAgentAsync(
            ChatAgentFactory.SqlAgentName,
            historicalPrompt,
            cancellationToken: ct);

        var supportText = (support.Text ?? string.Empty).Trim();

        if (LooksLikeClarifyingQuestion(supportText))
        {
            var retryPrompt = BuildForecastSupportDataPrompt(userText, request, stricter: true);

            support = await _caller.CallAgentAsync(
                ChatAgentFactory.SqlAgentName,
                retryPrompt,
                cancellationToken: ct);

            supportText = (support.Text ?? string.Empty).Trim();
        }

        if (!TryParseForecastInputPoints(supportText, out var inputPoints))
        {
            var failText = "I couldn't generate a forecast because the supporting query did not return a usable time series. Please ask for a forecast with a date-based metric such as monthly sales, revenue, or orders.";
            return await MaybeAttachDecisionCandidateAsync(userText, failText, ChatAgentFactory.ForecastingAgentName, routerReason, ct);
        }

        var forecast = ForecastingService.GenerateForecast(request, inputPoints);
        var markdown = BuildForecastMarkdown(forecast);

        try
        {
            var chartUrl = await CreateForecastChartAsync(forecast);
            markdown = $"![chart]({chartUrl})\n\n" + markdown;
            markdown += WrapHiddenToolPayload("chart_result", JsonSerializer.Serialize(new { type = "chart_url", url = chartUrl }));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[FORECAST_CHART_FALLBACK] " + ex);
        }

        markdown += WrapHiddenToolPayload("forecast_result", BuildForecastHiddenPayload(forecast));

        var checkedAnswer = await RunPpiSafeAsync(userText, markdown, ct);

        await TrackRouteAsync(ChatAgentFactory.ForecastingAgentName, "completed", ct);
        return await MaybeAttachDecisionCandidateAsync(
            userText,
            checkedAnswer,
            ChatAgentFactory.ForecastingAgentName,
            routerReason + " (grounded via DataExplorerAgent)",
            ct);
    }


    private async Task<PipelineResult> ExecuteAnomalyDetectionAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        var request = AnomalyRequestParser.Parse(userText);
        var supportPrompt = AnomalySupportPromptBuilder.Build(request);

        await TrackRouteAsync(ChatAgentFactory.SqlAgentName, "entered", ct);
        //await TrackRouteAsync(ChatAgentFactory.SqlAgentName, "segmentation support-data request", ct);
        //await TrackRouteAsync(ChatAgentFactory.SqlAgentName, "what-if support-data request", ct);
        var support = await _caller.CallAgentAsync(
            ChatAgentFactory.SqlAgentName,
            supportPrompt,
            cancellationToken: ct);

        var supportText = (support.Text ?? string.Empty).Trim();

        if (LooksLikeClarifyingQuestion(supportText))
        {
            var retryPrompt = AnomalySupportPromptBuilder.Build(request, stricter: true);
            await TrackRouteAsync(ChatAgentFactory.SqlAgentName, "entered", ct);
            //await TrackRouteAsync(ChatAgentFactory.SqlAgentName, "segmentation support-data retry", ct);
            //await TrackRouteAsync(ChatAgentFactory.SqlAgentName, "what-if support-data retry", ct);
            support = await _caller.CallAgentAsync(
                ChatAgentFactory.SqlAgentName,
                retryPrompt,
                cancellationToken: ct);
            supportText = (support.Text ?? string.Empty).Trim();
        }

        if (!AnomalyInputParser.TryParse(supportText, request.MetricName, request.GroupBy, out var dataset))
        {
            var failText = "I couldn't run anomaly detection because the supporting query did not return a usable time series. Please ask for anomalies on a date-based metric such as monthly sales, revenue, orders, cost, or headcount.";
            return await MaybeAttachDecisionCandidateAsync(userText, failText, ChatAgentFactory.AnomalyDetectionAgentName, routerReason, ct);
        }

        var coordinator = new AnomalyDetectionCoordinator();
        var result = coordinator.Execute(request, dataset);
        var markdown = result.Narrative;

        if (result.TableColumns.Count > 0 && result.TableRows.Count > 0)
            markdown += "\n\n" + BuildMarkdownTable(result.TableColumns, result.TableRows);

        try
        {
            var chartUrl = await CreateAnomalyChartAsync(result);
            markdown = $"![chart]({chartUrl})\n\n" + markdown;
            markdown += WrapHiddenToolPayload("chart_result", JsonSerializer.Serialize(new { type = "chart_url", url = chartUrl }));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ANOMALY_CHART_FALLBACK] " + ex);
        }

        markdown += WrapHiddenToolPayload("anomaly_result", AnomalyHiddenPayloadBuilder.Build(result));

        var checkedAnswer = await RunPpiSafeAsync(userText, markdown, ct);

        await TrackRouteAsync(ChatAgentFactory.AnomalyDetectionAgentName, "completed", ct);
        return await MaybeAttachDecisionCandidateAsync(
            userText,
            checkedAnswer,
            ChatAgentFactory.AnomalyDetectionAgentName,
            routerReason + " (grounded via DataExplorerAgent)",
            ct);
    }


    private async Task<PipelineResult> ExecuteSegmentationAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        await TrackRouteAsync(ChatAgentFactory.DataSegmentationAgentName, "entered", ct);

        var supportPrompt = BuildSegmentationSupportDataPrompt(userText);

        await TrackRouteAsync(ChatAgentFactory.SqlAgentName, "entered", ct);
        var support = await _caller.CallAgentAsync(
            ChatAgentFactory.SqlAgentName,
            supportPrompt,
            cancellationToken: ct);

        var supportText = (support.Text ?? string.Empty).Trim();

        if (LooksLikeClarifyingQuestion(supportText))
        {
            Console.WriteLine("[SEGMENTATION_RETRY] SqlAgent returned a question. Retrying with stricter enforcement.");

            var retryPrompt = BuildSegmentationSupportDataPrompt(userText, stricter: true);

            await TrackRouteAsync(ChatAgentFactory.SqlAgentName, "entered", ct);
            support = await _caller.CallAgentAsync(
                ChatAgentFactory.SqlAgentName,
                retryPrompt,
                cancellationToken: ct);

            supportText = (support.Text ?? string.Empty).Trim();
        }

        if (!TryParseSegmentationRows(supportText, out var rows, out var parseReason))
        {
            var failText = "I couldn't run segmentation because the supporting query did not return a usable grouped dataset. Please ask for a segmentation such as sales by region, revenue by category, orders by status, or headcount by department.";
            return await MaybeAttachDecisionCandidateAsync(userText, failText, "DataSegmentationAgent", routerReason + $" ({parseReason})", ct);
        }

        var markdown = BuildSegmentationMarkdown(userText, rows);

        try
        {
            var chartUrl = await CreateSegmentationChartAsync(userText, rows);
            markdown = $"![chart]({chartUrl})\n\n" + markdown;
            markdown += WrapHiddenToolPayload("chart_result", JsonSerializer.Serialize(new { type = "chart_url", url = chartUrl }));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[SEGMENTATION_CHART_FALLBACK] " + ex);
        }

        markdown += WrapHiddenToolPayload("segmentation_result", BuildSegmentationHiddenPayload(userText, rows));

        var checkedAnswer = await RunPpiSafeAsync(userText, markdown, ct);

        await TrackRouteAsync("DataSegmentationAgent", "completed", ct);
        return await MaybeAttachDecisionCandidateAsync(
            userText,
            checkedAnswer,
            "DataSegmentationAgent",
            routerReason + " (grounded via SqlAgent)",
            ct);
    }

    private static bool ShouldForceSqlDataExplorer(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var t = userText.Trim().ToLowerInvariant();

        if (LooksLikeSchemaAdviceQuestion(t))
            return false;

        return true;
    }

    private static bool LooksLikeSchemaAdviceQuestion(string t)
    {
        if (string.IsNullOrWhiteSpace(t))
            return false;

        t = t.ToLowerInvariant().Trim();

        // Explicit schema/advice phrases
        if (t.Contains("what tables") ||
            t.Contains("which tables") ||
            t.Contains("how tables relate") ||
            t.Contains("schema") ||
            t.Contains("relationship") ||
            t.Contains("relationships") ||
            t.Contains("what joins") ||
            t.Contains("which joins") ||
            t.Contains("what columns") ||
            t.Contains("which columns") ||
            t.Contains("how to analyze"))
            return true;

        // Profiling / description style requests
        if (t.StartsWith("profile ") ||
            t.StartsWith("describe ") ||
            t.StartsWith("explain ") ||
            t.StartsWith("show columns") ||
            t.StartsWith("table structure") ||
            t.StartsWith("metadata") ||
            t.StartsWith("ddl"))
            return true;

        // Conceptual relationship questions
        if ((t.Contains("how ") && t.Contains(" relate ")) ||
            (t.Contains("how ") && t.Contains(" relates ")) ||
            (t.Contains("how ") && t.Contains(" related ")) ||
            (t.Contains("how ") && t.Contains(" connect ")) ||
            (t.Contains("how ") && t.Contains(" connected ")) ||
            (t.Contains("how ") && t.Contains(" impact ")) ||
            (t.Contains("how ") && t.Contains(" impacts ")) ||
            (t.Contains("how ") && t.Contains(" tie to ")) ||
            (t.Contains("how ") && t.Contains(" linked to ")))
            return true;

        // Schema-qualified table reference
        if (System.Text.RegularExpressions.Regex.IsMatch(
                t,
                @"\b[a-z0-9_]+\.[a-z0-9_]+\b"))
            return true;

        return false;
    }

    private static string BuildSegmentationSupportDataPrompt(string userText, bool stricter = false)
    {
        var strict = stricter
            ? "- You previously asked a question. That is NOT allowed. Choose the most reasonable interpretation and proceed."
            : "- Do NOT ask follow-up questions. Choose the most reasonable interpretation and proceed.";

        return $@"
You are the SQL agent for grouped segmentation analysis.

NON-NEGOTIABLE RULES:
{strict}
- Use database tools only.
- Return a grouped result suitable for segmentation analysis.
- Prefer one clear segment dimension and one numeric metric.
- Limit the result to the most relevant 10-20 segments unless the user explicitly asks otherwise.
- Return the segments ordered by Value descending when that makes sense.
- This system uses SQL Server syntax.
- If the request is customer segmentation using spend/frequency/recency or quantile-style labels, build the SQL in a SQL Server-safe way.
- For SQL Server, PERCENTILE_CONT and PERCENTILE_DISC MUST include OVER(...).
- Never generate PERCENTILE_CONT / PERCENTILE_DISC as a scalar subquery without OVER(...).
- For percentile threshold segmentation, use this safe pattern:
  1) BaseData CTE with one row per customer/entity and metrics like TotalSpend, OrderFrequency, Recency.
  2) Thresholds CTE using SELECT DISTINCT with PERCENTILE_CONT(... ) WITHIN GROUP (...) OVER () for each threshold.
  3) CROSS JOIN Thresholds into the final CASE-based labeling query.
- Example safe threshold pattern:
  WITH BaseData AS (...),
  Thresholds AS (
      SELECT DISTINCT
          PERCENTILE_CONT(0.8) WITHIN GROUP (ORDER BY TotalSpend) OVER () AS TotalSpendP80,
          PERCENTILE_CONT(0.8) WITHIN GROUP (ORDER BY OrderFrequency) OVER () AS OrderFrequencyP80,
          PERCENTILE_CONT(0.2) WITHIN GROUP (ORDER BY Recency) OVER () AS RecencyP20
      FROM BaseData
  )
  SELECT Segment, COUNT(*) AS Value, 100.0 * COUNT(*) / SUM(COUNT(*)) OVER () AS SharePct,
         DENSE_RANK() OVER (ORDER BY COUNT(*) DESC) AS Rank
  FROM (... CASE labels using CROSS JOIN Thresholds ...)
  GROUP BY Segment
  ORDER BY Value DESC;
- If the SQL execution fails, correct the SQL and retry once using valid SQL Server syntax.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

MANDATORY OUTPUT RULES:
- Return ONLY the JSON returned by ExecuteSelectAsync.
- No markdown, no prose, no wrapping.
- The returned rows MUST represent grouped segmentation data.
- Use EXACT logical columns whenever possible:
  - Segment: the segment/group label
  - Value: the numeric measure for that segment
  - SharePct: optional percentage share of total
  - Rank: optional rank
- If exact aliases are not possible, still return one categorical grouping column and one numeric measure column.

USER REQUEST:
{userText}
";
    }

    private static bool TryParseSegmentationRows(string text, out List<SegmentationRow> rows, out string reason)
    {
        rows = new List<SegmentationRow>();
        reason = "no grouped rows parsed";

        var json = ExtractFirstJsonObject(text) ?? (LooksLikeJson(text) ? text : null);
        if (string.IsNullOrWhiteSpace(json))
        {
            reason = "no json payload";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetPropIgnoreCase(doc.RootElement, "rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
            {
                reason = "rows array missing";
                return false;
            }

            var rawRows = rowsEl.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object).ToList();
            if (rawRows.Count == 0)
            {
                reason = "rows array empty";
                return false;
            }

            foreach (var row in rawRows)
            {
                var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in row.EnumerateObject())
                    map[p.Name] = p.Value;

                if (!TryResolveSegmentLabel(map, out var segment))
                    continue;

                if (!TryResolveNumericValue(map, out var value, out var valueColumn))
                    continue;

                double? sharePct = null;
                if (TryResolveOptionalNumeric(map, new[] { "SharePct", "Share", "Percentage", "Percent", "Pct", "ContributionPct" }, out var s))
                    sharePct = s;

                int? rank = null;
                if (TryResolveOptionalInteger(map, new[] { "Rank", "RowNum", "Position" }, out var rnk))
                    rank = rnk;

                rows.Add(new SegmentationRow(segment, value, sharePct, rank, valueColumn));
            }

            if (rows.Count == 0)
            {
                reason = "no segment/value pairs found";
                return false;
            }

            rows = rows
                .OrderBy(r => r.Rank ?? int.MaxValue)
                .ThenByDescending(r => r.Value)
                .ThenBy(r => r.Segment, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var total = rows.Sum(r => r.Value);
            if (total > 0)
            {
                rows = rows.Select(r =>
                    r.SharePct.HasValue
                        ? r
                        : r with { SharePct = Math.Round((r.Value / total) * 100d, 2) })
                    .ToList();
            }

            reason = "ok";
            return true;
        }
        catch
        {
            reason = "invalid json payload";
            return false;
        }
    }

    private static string BuildSegmentationMarkdown(string userText, List<SegmentationRow> rows)
    {
        var topRows = rows.Take(20).ToList();
        var total = rows.Sum(r => r.Value);
        var top = rows.OrderByDescending(r => r.Value).First();
        var bottom = rows.OrderBy(r => r.Value).First();
        var top3Share = total > 0
            ? rows.OrderByDescending(r => r.Value).Take(3).Sum(r => r.Value) / total * 100d
            : 0d;

        var metricLabel = GuessSegmentationMetricLabel(userText, rows);
        var sb = new StringBuilder();

        sb.AppendLine("Segmentation Summary");
        sb.AppendLine($"The largest segment is **{EscapeMd(top.Segment)}** with **{FormatMetricValue(top.Value)}** {EscapeMd(metricLabel)}.");
        sb.AppendLine($"The smallest segment is **{EscapeMd(bottom.Segment)}** with **{FormatMetricValue(bottom.Value)}** {EscapeMd(metricLabel)}.");
        if (total > 0)
            sb.AppendLine($"The top 3 segments contribute **{top3Share:0.##}%** of the total.");
        sb.AppendLine();

        sb.AppendLine("Key Insights");
        sb.AppendLine($"• Total across all returned segments: **{FormatMetricValue(total)}** {EscapeMd(metricLabel)}.");
        sb.AppendLine($"• Number of returned segments: **{rows.Count}**.");
        if (top.SharePct.HasValue)
            sb.AppendLine($"• {EscapeMd(top.Segment)} contributes **{top.SharePct.Value:0.##}%** of the total.");
        if (rows.Count >= 2)
        {
            var second = rows.OrderByDescending(r => r.Value).Skip(1).First();
            var gap = top.Value - second.Value;
            sb.AppendLine($"• Gap between the top two segments: **{FormatMetricValue(gap)}** {EscapeMd(metricLabel)}.");
        }
        sb.AppendLine();

        sb.AppendLine("| Segment | Value | Share % | Rank |");
        sb.AppendLine("| --- | ---: | ---: | ---: |");
        var rankCounter = 1;
        foreach (var row in topRows)
        {
            var share = row.SharePct.HasValue ? row.SharePct.Value.ToString("0.##") : "";
            var rank = row.Rank?.ToString() ?? rankCounter.ToString();
            sb.AppendLine($"| {EscapeMd(row.Segment)} | {FormatMetricValue(row.Value)} | {share} | {rank} |");
            rankCounter++;
        }

        if (rows.Count > topRows.Count)
            sb.AppendLine($"\nShowing first {topRows.Count} rows of {rows.Count} total segments.");

        return sb.ToString().Trim();
    }

    private async Task<string> CreateSegmentationChartAsync(string userText, List<SegmentationRow> rows)
    {
        var chartRows = rows
            .OrderByDescending(r => r.Value)
            .Take(12)
            .ToList();

        if (chartRows.Count == 0)
            throw new InvalidOperationException("No segmentation rows available for chart.");

        var labels = chartRows.Select(r => r.Segment).ToArray();
        var values = chartRows.Select(r => r.Value).ToArray();

        return await _chartTools.CreateBarChartPngAsync(
            GuessSegmentationChartTitle(userText),
            "Segment",
            GuessSegmentationMetricLabel(userText, rows),
            labels,
            values,
            width: 1000,
            height: 600);
    }

    private static string BuildSegmentationHiddenPayload(string userText, List<SegmentationRow> rows)
    {
        return JsonSerializer.Serialize(new
        {
            type = "segmentation_result",
            title = GuessSegmentationChartTitle(userText),
            metric = GuessSegmentationMetricLabel(userText, rows),
            segments = rows.Select(r => new
            {
                segment = r.Segment,
                value = r.Value,
                sharePct = r.SharePct,
                rank = r.Rank
            }).ToList()
        });
    }

    private static bool TryResolveSegmentLabel(Dictionary<string, JsonElement> map, out string segment)
    {
        foreach (var key in new[] { "Segment", "Category", "Group", "Series", "Label", "Name", "Type", "Status", "Region", "Territory", "Department" })
        {
            if (map.TryGetValue(key, out var el))
            {
                segment = el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.ToString();
                if (!string.IsNullOrWhiteSpace(segment))
                    return true;
            }
        }

        foreach (var kvp in map)
        {
            if (kvp.Value.ValueKind == JsonValueKind.String)
            {
                segment = kvp.Value.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(segment))
                    return true;
            }
        }

        segment = "";
        return false;
    }

    private static bool TryResolveNumericValue(Dictionary<string, JsonElement> map, out double value, out string valueColumn)
    {
        foreach (var key in new[] { "Value", "Amount", "Metric", "Total", "Count", "Sales", "Revenue", "Orders", "Headcount" })
        {
            if (map.TryGetValue(key, out var el) && TryGetDoubleValue(el, out value))
            {
                valueColumn = key;
                return true;
            }
        }

        foreach (var kvp in map)
        {
            if (TryGetDoubleValue(kvp.Value, out value))
            {
                valueColumn = kvp.Key;
                return true;
            }
        }

        value = 0;
        valueColumn = "Value";
        return false;
    }

    private static bool TryResolveOptionalNumeric(Dictionary<string, JsonElement> map, IEnumerable<string> keys, out double value)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var el) && TryGetDoubleValue(el, out value))
                return true;
        }

        value = 0;
        return false;
    }

    private static bool TryResolveOptionalInteger(Dictionary<string, JsonElement> map, IEnumerable<string> keys, out int value)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value))
                    return true;
                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out value))
                    return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryGetDoubleValue(JsonElement el, out double value)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value))
            return true;

        if (el.ValueKind == JsonValueKind.String)
        {
            var s = (el.GetString() ?? "").Replace(",", "").Replace("%", "").Trim();
            if (double.TryParse(s, out value))
                return true;
        }

        value = 0;
        return false;
    }

    private static string GuessSegmentationMetricLabel(string userText, List<SegmentationRow> rows)
    {
        var t = (userText ?? string.Empty).ToLowerInvariant();

        if (t.Contains("sales")) return "sales";
        if (t.Contains("revenue")) return "revenue";
        if (t.Contains("order")) return "orders";
        if (t.Contains("headcount") || t.Contains("employee")) return "headcount";
        if (t.Contains("cost")) return "cost";
        if (rows.Count > 0 && !string.IsNullOrWhiteSpace(rows[0].ValueColumn))
            return rows[0].ValueColumn;

        return "value";
    }
    private static string GuessSegmentationChartTitle(string userText)
    {
        var cleaned = Regex.Replace((userText ?? "Segmentation").Trim(), @"\s+", " ");
        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }

    private static string FormatMetricValue(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 0.0000001d)
            return Math.Round(value).ToString("0");

        return value.ToString("0.##");
    }

    private sealed record SegmentationRow(
        string Segment,
        double Value,
        double? SharePct,
        int? Rank,
        string ValueColumn);

    private async Task<string> CreateAnomalyChartAsync(AnomalyDetectionResult result)
    {
        if (!result.HasChart)
            throw new InvalidOperationException("No anomaly chart payload available.");

        if (result.SeriesNames.Count > 1)
        {
            return await _chartTools.CreateMultiSeriesLineChartPngAsync(
                result.ChartTitle,
                "Period",
                "Value",
                result.ChartLabels.ToArray(),
                result.SeriesNames.ToArray(),
                result.SeriesValues.ToArray(),
                width: 1000,
                height: 600);
        }

        return await _chartTools.CreateLineChartPngAsync(
            result.ChartTitle,
            "Period",
            "Value",
            result.ChartLabels.ToArray(),
            result.SeriesValues[0],
            width: 1000,
            height: 600);
    }

    private static string BuildMarkdownTable(IReadOnlyList<string> columns, IReadOnlyList<Dictionary<string, string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", columns.Select(EscapeMd)) + " |");
        sb.AppendLine("| " + string.Join(" | ", columns.Select(_ => "---")) + " |");
        foreach (var row in rows)
        {
            var cells = columns.Select(c => EscapeMd(row.TryGetValue(c, out var v) ? v : string.Empty));
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
        }
        return sb.ToString().Trim();
    }

    private static string BuildForecastSupportDataPrompt(string userText, ForecastRequest request, bool stricter = false)
    {
        var seriesLine = request.GroupBy is null
            ? "- Return rows with EXACT columns: Period, Value."
            : $"- Return rows with EXACT columns: Period, Value, Series. Series MUST contain the grouping label for '{request.GroupBy}'.";

        var stricterLine = stricter
            ? "- You previously asked a question. That is NOT allowed. Choose the most reasonable interpretation and proceed."
            : "- Do NOT ask follow-up questions. Choose the most reasonable interpretation and proceed.";

        return $@"
You are the SQL data exploration agent for forecasting support data.

{stricterLine}

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

MANDATORY OUTPUT RULES:
- Return ONLY the JSON returned by ExecuteSelectAsync.
- No markdown, no prose, no wrapping.
- Aggregate the data into a time series suitable for forecasting.
- Bucket by {request.Grain.ToString().ToLowerInvariant()}.
- Return historical data ordered by Period ascending.
{seriesLine}
- Period MUST be a date-like bucket label or ISO date string.
- Value MUST be numeric.
- Include enough history to support forecasting, preferably the last {request.HistoryPeriods} periods.

USER REQUEST:
{userText}
";
    }

    private static bool TryParseForecastInputPoints(string text, out List<ForecastInputPoint> points)
    {
        points = new List<ForecastInputPoint>();
        var json = ExtractFirstJsonObject(text) ?? (LooksLikeJson(text) ? text : null);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetPropIgnoreCase(doc.RootElement, "rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var row in rowsEl.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;

                if (!TryGetPropIgnoreCase(row, "Period", out var periodEl))
                    continue;
                if (!TryGetPropIgnoreCase(row, "Value", out var valueEl))
                    continue;

                var periodText = periodEl.ValueKind == JsonValueKind.String ? (periodEl.GetString() ?? "") : periodEl.ToString();
                if (!DateTime.TryParse(periodText, out var period))
                    continue;

                double value;
                if (valueEl.ValueKind == JsonValueKind.Number && valueEl.TryGetDouble(out var numeric))
                    value = numeric;
                else if (valueEl.ValueKind == JsonValueKind.String && double.TryParse(valueEl.GetString(), out var parsed))
                    value = parsed;
                else
                    continue;

                string? series = null;
                if (TryGetPropIgnoreCase(row, "Series", out var seriesEl))
                    series = seriesEl.ValueKind == JsonValueKind.String ? seriesEl.GetString() : seriesEl.ToString();

                points.Add(new ForecastInputPoint(period, value, string.IsNullOrWhiteSpace(series) ? null : series));
            }

            return points.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildForecastMarkdown(ForecastResult forecast)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Forecast Summary");
        sb.AppendLine(forecast.Summary);
        sb.AppendLine();

        if (forecast.Assumptions.Count > 0)
        {
            sb.AppendLine("Assumptions");
            foreach (var item in forecast.Assumptions)
                sb.AppendLine($"• {item}");
            sb.AppendLine();
        }

        var topRows = forecast.Points
            .OrderBy(p => p.Series ?? "")
            .ThenBy(p => p.Period)
            .Take(60)
            .ToList();

        sb.AppendLine("| Series | Period | Type | Value | Lower | Upper | Scenario |");
        sb.AppendLine("| --- | --- | --- | ---: | ---: | ---: | --- |");
        foreach (var p in topRows)
        {
            sb.AppendLine($"| {EscapeMd(p.Series ?? "Overall")} | {p.Period:yyyy-MM-dd} | {(p.IsForecast ? "Forecast" : "History")} | {p.Value:0.##} | {(p.LowerBound.HasValue ? p.LowerBound.Value.ToString("0.##") : "")} | {(p.UpperBound.HasValue ? p.UpperBound.Value.ToString("0.##") : "")} | {EscapeMd(p.Scenario ?? "")} |");
        }

        if (forecast.Points.Count > topRows.Count)
            sb.AppendLine($"\nShowing first {topRows.Count} rows of {forecast.Points.Count} total forecast rows.");

        return sb.ToString().Trim();
    }

    private async Task<string> CreateForecastChartAsync(ForecastResult forecast)
    {
        var forecastOnly = forecast.Points.Where(p => p.IsForecast).ToList();
        var history = forecast.Points.Where(p => !p.IsForecast).ToList();

        var allPeriods = forecast.Points.Select(p => p.Period).Distinct().OrderBy(d => d).ToList();
        var labels = allPeriods.Select(d => d.ToString("yyyy-MM-dd")).ToArray();

        if (forecast.Grouped)
        {
            var chosenSeries = forecast.Points
                .Select(p => p.Series ?? "Overall")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();

            var seriesNames = new List<string>();
            var seriesValues = new List<double[]>();

            foreach (var s in chosenSeries)
            {
                var values = allPeriods
                    .Select(period =>
                    {
                        var point = forecastOnly.FirstOrDefault(p => string.Equals(p.Series ?? "Overall", s, StringComparison.OrdinalIgnoreCase) && p.Period == period);
                        return point?.Value ?? double.NaN;
                    })
                    .Select(v => double.IsNaN(v) ? 0d : v)
                    .ToArray();

                if (values.Any(v => v != 0d))
                {
                    seriesNames.Add(s);
                    seriesValues.Add(values);
                }
            }

            if (seriesNames.Count == 0)
                throw new InvalidOperationException("No forecast series available for chart.");

            return await _chartTools.CreateMultiSeriesLineChartPngAsync(
                forecast.Title,
                "Period",
                yAxisLabel: "Value",
                xLabels: labels,
                seriesNames: seriesNames.ToArray(),
                seriesValues: seriesValues.ToArray(),
                width: 1000,
                height: 600);
        }
        else
        {
            var values = allPeriods
                .Select(period =>
                {
                    var hist = history.FirstOrDefault(p => p.Period == period && string.Equals(p.Scenario, "Baseline", StringComparison.OrdinalIgnoreCase));
                    if (hist is not null) return hist.Value;
                    var f = forecastOnly.FirstOrDefault(p => p.Period == period && string.Equals(p.Scenario, "Baseline", StringComparison.OrdinalIgnoreCase));
                    return f?.Value ?? 0d;
                })
                .ToArray();

            return await _chartTools.CreateLineChartPngAsync(
                forecast.Title,
                "Period",
                "Value",
                labels,
                values,
                width: 1000,
                height: 600);
        }
    }

    private static string BuildForecastHiddenPayload(ForecastResult forecast)
    {
        return JsonSerializer.Serialize(new
        {
            type = "forecast_result",
            title = forecast.Title,
            summary = forecast.Summary,
            grouped = forecast.Grouped,
            points = forecast.Points.Select(p => new
            {
                period = p.Period,
                series = p.Series,
                value = p.Value,
                lowerBound = p.LowerBound,
                upperBound = p.UpperBound,
                isForecast = p.IsForecast,
                scenario = p.Scenario
            }).ToList()
        });
    }




}
