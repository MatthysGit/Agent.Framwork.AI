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
    private async Task<PipelineResult> ExecuteWhatIfSimulationAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        var request = WhatIfSimulationService.ParseRequest(userText);
        var supportPrompt = BuildWhatIfSupportDataPrompt(userText, request);

        var support = await _caller.CallAgentAsync(
            ChatAgentFactory.SqlAgentName,
            supportPrompt,
            cancellationToken: ct);

        var supportText = (support.Text ?? string.Empty).Trim();

        if (LooksLikeClarifyingQuestion(supportText))
        {
            var retryPrompt = BuildWhatIfSupportDataPrompt(userText, request, stricter: true);

            support = await _caller.CallAgentAsync(
                ChatAgentFactory.SqlAgentName,
                retryPrompt,
                cancellationToken: ct);

            supportText = (support.Text ?? string.Empty).Trim();
        }

        if (!WhatIfSimulationService.TryParseInputPoints(supportText, out var inputPoints))
        {
            var failText = "I couldn't run the what-if simulation because the supporting query did not return a usable baseline dataset. Please ask for a simulation on a numeric metric such as sales, revenue, cost, profit, orders, or headcount.";
            return await MaybeAttachDecisionCandidateAsync(userText, failText, ChatAgentFactory.WhatIfSimulationAgentName, routerReason, ct);
        }

        var simulation = WhatIfSimulationService.Run(request, inputPoints);
        var markdown = WhatIfSimulationService.BuildMarkdown(simulation);

        try
        {
            var chartUrl = await CreateWhatIfChartAsync(simulation);
            markdown = $"![chart]({chartUrl})\n\n" + markdown;
            markdown += WrapHiddenToolPayload("chart_result", JsonSerializer.Serialize(new { type = "chart_url", url = chartUrl }));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[WHATIF_CHART_FALLBACK] " + ex);
        }

        markdown += WrapHiddenToolPayload("whatif_result", WhatIfSimulationService.BuildHiddenPayload(simulation));

        var checkedAnswer = await RunPpiSafeAsync(userText, markdown, ct);

        await TrackRouteAsync(ChatAgentFactory.WhatIfSimulationAgentName, "completed", ct);
        return await MaybeAttachDecisionCandidateAsync(
            userText,
            checkedAnswer,
            ChatAgentFactory.WhatIfSimulationAgentName,
            routerReason + " (grounded via SqlAgent)",
            ct);
    }

    private static string BuildWhatIfSupportDataPrompt(string userText, WhatIfRequest request, bool stricter = false)
    {
        var strict = stricter
            ? "- You previously asked a question. That is NOT allowed. Choose the most reasonable interpretation and proceed."
            : "- Do NOT ask follow-up questions. Choose the most reasonable interpretation and proceed.";

        var groupingLine = string.IsNullOrWhiteSpace(request.GroupBy)
            ? "- Prefer a baseline time series if the schema supports it. Otherwise return the most relevant grouped baseline."
            : $"- If possible, group the returned baseline by '{request.GroupBy}' or the closest matching business dimension.";

        return $@"
You are the SQL agent for baseline what-if simulation support data.

NON-NEGOTIABLE RULES:
{strict}
- Use database tools only.
- Return a baseline dataset that can be used for deterministic what-if simulation.
- Prefer a single numeric metric and one clear label/dimension.
{groupingLine}
- Include enough rows to make the simulation meaningful, typically 8-24 rows for time series or 5-15 rows for grouped results.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

MANDATORY OUTPUT RULES:
- Return ONLY the JSON returned by ExecuteSelectAsync.
- No markdown, no prose, no wrapping.
- The returned rows MUST include:
  - Label: the baseline bucket label (for example month, territory, department, category, or other grouping label)
  - Value: the numeric baseline measure
  - Series: optional secondary grouping label if useful
- If exact aliases are not possible, still return one label-like column and one numeric measure column.
- Order the rows logically (time ascending when time-like, otherwise by relevance).

USER REQUEST:
{userText}

SIMULATION CONTEXT:
- Target metric: {request.MetricName}
- Changed variable: {request.VariableName}
- Scenario: {request.ScenarioName}
";
    }

    private async Task<string> CreateWhatIfChartAsync(WhatIfSimulationResult result)
    {
        var points = result.Points
            .OrderBy(p => p.Series ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.SortDate ?? DateTime.MaxValue)
            .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (points.Count == 0)
            throw new InvalidOperationException("No simulation rows available for chart.");

        if (result.Grouped)
        {
            var groups = points
                .GroupBy(p => p.Series ?? "Overall", StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();

            var labels = points
                .Where(p => string.Equals(p.Series ?? "Overall", groups[0].Key, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Label)
                .ToArray();

            var seriesNames = new List<string>();
            var seriesValues = new List<double[]>();

            foreach (var g in groups)
            {
                var values = labels
                    .Select(label => g.FirstOrDefault(p => string.Equals(p.Label, label, StringComparison.OrdinalIgnoreCase))?.ScenarioValue ?? 0d)
                    .ToArray();

                if (values.Any(v => v != 0d))
                {
                    seriesNames.Add(g.Key);
                    seriesValues.Add(values);
                }
            }

            if (result.TimeSeriesLike)
            {
                return await _chartTools.CreateMultiSeriesLineChartPngAsync(
                    result.Title,
                    "Label",
                    result.MetricLabel,
                    labels,
                    seriesNames.ToArray(),
                    seriesValues.ToArray(),
                    width: 1000,
                    height: 600);
            }

            return await _chartTools.CreateMultiSeriesColumnChartPngAsync(
                result.Title,
                "Label",
                result.MetricLabel,
                labels,
                seriesNames.ToArray(),
                seriesValues.ToArray(),
                width: 1000,
                height: 600);
        }

        var top = result.TimeSeriesLike
            ? points
            : points.OrderByDescending(p => Math.Abs(p.DeltaValue)).Take(12).ToList();

        var xLabels = top.Select(p => p.Label).ToArray();
        var scenarioValues = top.Select(p => p.ScenarioValue).ToArray();

        if (result.TimeSeriesLike)
        {
            return await _chartTools.CreateLineChartPngAsync(
                result.Title,
                "Label",
                result.MetricLabel,
                xLabels,
                scenarioValues,
                width: 1000,
                height: 600);
        }

        return await _chartTools.CreateBarChartPngAsync(
            result.Title,
            "Label",
            result.MetricLabel,
            xLabels,
            scenarioValues,
            width: 1000,
            height: 600);
    }

    private static string BuildDataIntelligenceSupportDataPrompt(string userText, bool stricter = false)
    {
        var strict = stricter
            ? "- You previously asked a question. That is NOT allowed. Choose the most reasonable interpretation and proceed."
            : "- Do NOT ask follow-up questions. Choose the most reasonable interpretation and proceed.";

        return $@"
You are the SQL data exploration agent for analytical support data.

NON-NEGOTIABLE RULES:
{strict}
- Use database tools only.
- Return the most relevant grounded dataset for the user's analysis request.
- Prefer compact, high-signal output over broad dumps.
- Choose the most business-relevant dimensions and metrics.
- When possible, return grouped or trend-oriented results that support interpretation.
- Limit output to a practical size unless the user explicitly asks for full detail.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

OUTPUT RULE (MANDATORY):
- You MUST return ONLY the JSON returned by ExecuteSelectAsync (SqlServerSelectTool).
- No markdown, no prose, no extra keys, no wrapping.

USER REQUEST:
{userText}
";
    }

    private static string PrepareDataIntelligenceSupportPayload(string userText, string supportText)
    {
        if (string.IsNullOrWhiteSpace(supportText))
        {
            return $$"""
            {
              "userRequest": {{JsonSerializer.Serialize(userText)}},
              "status": "no_support_data",
              "supportData": null
            }
            """;
        }

        var trimmed = supportText.Trim();

        if (string.Equals(trimmed, "unauthorized access", StringComparison.OrdinalIgnoreCase))
        {
            return $$"""
            {
              "userRequest": {{JsonSerializer.Serialize(userText)}},
              "status": "unauthorized",
              "supportData": null
            }
            """;
        }

        var json = ExtractFirstJsonObject(trimmed);
        if (!string.IsNullOrWhiteSpace(json))
        {
            return $$"""
            {
              "userRequest": {{JsonSerializer.Serialize(userText)}},
              "status": "ok",
              "supportData": {{json}}
            }
            """;
        }

        if (LooksLikeJson(trimmed))
        {
            return $$"""
            {
              "userRequest": {{JsonSerializer.Serialize(userText)}},
              "status": "ok",
              "supportData": {{trimmed}}
            }
            """;
        }

        return $$"""
        {
          "userRequest": {{JsonSerializer.Serialize(userText)}},
          "status": "unstructured_support",
          "supportText": {{JsonSerializer.Serialize(supportText)}}
        }
        """;
    }

    private static string BuildDataIntelligenceSynthesisPrompt(string userText, string supportPayload)
    {
        return $@"
You are producing a grounded data intelligence analysis.

Your task:
- Interpret the support payload.
- Explain the most meaningful signals in business terms.
- Highlight concentration, mix, change, ranking, imbalance, volatility, or operational concerns if present.
- Be explicit about uncertainty when the payload is narrow or incomplete.
- Do not invent calculations that are not directly supported.

Required markdown structure:

## Data Intelligence Summary
A concise summary of what matters most.

## What the data suggests
2-4 bullets.

## Key signals / patterns
2-5 bullets.

## Risks or watchouts
2-4 bullets.

## Recommended actions
2-4 bullets.

User request:
{userText}

Support payload:
{supportPayload}
";
    }

    private static string BuildExecutiveSupportDataPrompt(string userText, bool stricter = false)
    {
        var focus = InferExecutiveFocus(userText);

        var focusGuidance = focus switch
        {
            "sales" => @"
Preferred supporting datasets (pick the best 2-3 that the database can answer):
1. Overall sales KPI summary (for example total sales, order count, average order value if available).
2. Sales breakdown by territory / region / sales area (top 5-10).
3. Sales trend by month or by year-month for the latest available periods.
4. Top product categories or top products by sales if relevant.
",
            "customer" => @"
Preferred supporting datasets (pick the best 2-3 that the database can answer):
1. Customer count or customer segmentation summary.
2. Top customers by sales / order volume.
3. Customer purchasing trend over time.
4. Geographic or territory distribution of customers if relevant.
",
            "product" => @"
Preferred supporting datasets (pick the best 2-3 that the database can answer):
1. Top products or categories by sales / orders.
2. Product-category performance comparison.
3. Product sales trend over time if relevant.
",
            "workforce" => @"
Preferred supporting datasets (pick the best 2-3 that the database can answer):
1. Headcount summary.
2. Breakdown by department / title / gender / region if available.
3. Any visible concentration or imbalance supported by the data.
",
            _ => @"
Preferred supporting datasets (pick the best 2-3 that the database can answer):
1. One overall KPI summary relevant to the user request.
2. One grouped breakdown by the most important business dimension.
3. One time trend if the schema supports it.
"
        };

        var strict = stricter
            ? "You previously returned a clarifying question. That is NOT allowed. You MUST proceed with the most reasonable interpretation and return data now."
            : "You MUST NOT ask clarifying questions. If the request is broad, choose the most reasonable interpretation and proceed.";

        return $@"
You are gathering supporting facts for an executive summary from THIS application's database.

NON-NEGOTIABLE RULES:
- {strict}
- Use database tools only.
- Prefer compact, executive-relevant result sets.
- Limit grouped tables to about 5-10 rows.
- Limit trend tables to about 12 periods.
- Return ONLY strict JSON. No markdown. No prose. No code fences.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

BUSINESS FOCUS:
{focus}

{focusGuidance}

Return ONLY JSON in EXACTLY this schema:
{{
  ""focus"": ""{focus}"",
  ""summary"": ""<very short factual description>"",
  ""datasets"": [
    {{
      ""title"": ""<dataset title>"",
      ""columns"": [""Col1"", ""Col2""],
      ""rows"": [
        [""A"", ""1""],
        [""B"", ""2""]
      ]
    }}
  ]
}}

USER REQUEST:
{userText}
";
    }

    private static string PrepareExecutiveSupportPayload(string userText, string supportText)
    {
        if (string.IsNullOrWhiteSpace(supportText))
            return @"{""focus"":""unknown"",""summary"":""No supporting data returned."",""datasets"":[]}";

        var trimmed = supportText.Trim();

        if (string.Equals(trimmed, "unauthorized access", StringComparison.OrdinalIgnoreCase))
            return @"{""focus"":""unknown"",""summary"":""Database access was not authorized."",""datasets"":[]}";

        var json = ExtractFirstJsonObject(trimmed);
        if (!string.IsNullOrWhiteSpace(json))
            return TruncateForExecutiveInput(json);

        if (LooksLikeJson(trimmed))
            return TruncateForExecutiveInput(trimmed);

        if (TryRenderRowsObjectTableMarkdown(trimmed, out var mdFromRows))
        {
            return JsonSerializer.Serialize(new
            {
                focus = InferExecutiveFocus(userText),
                summary = "Supporting data returned as SQL rows.",
                datasets = new[]
                {
                    new
                    {
                        title = "SQL result",
                        columns = Array.Empty<string>(),
                        rows = Array.Empty<string[]>(),
                        markdown = mdFromRows
                    }
                }
            });
        }

        return JsonSerializer.Serialize(new
        {
            focus = InferExecutiveFocus(userText),
            summary = "Supporting data returned in text form.",
            datasets = new[]
            {
                new
                {
                    title = "Supporting result",
                    text = TruncateForExecutiveInput(trimmed)
                }
            }
        });
    }

    private static string BuildExecutiveSynthesisPrompt(string userText, string supportJson)
    {
        return $@"
Create a leadership-ready executive summary using ONLY the supporting data below.

Requirements:
- Be grounded in the data only.
- Do NOT invent numbers, trends, causes, or KPIs.
- If a cause is only a plausible interpretation, phrase it cautiously using terms like 'may' or 'could'.
- If the data is limited, say so clearly.
- Keep the tone concise, executive, and decision-oriented.

Use EXACTLY this structure:

Executive Summary
<2-3 sentence summary>

Key Insights
• Insight 1
• Insight 2
• Insight 3

Risks
• Risk 1
• Risk 2

Opportunities
• Opportunity 1
• Opportunity 2

Recommended Actions
• Action 1
• Action 2

Original user request:
{userText}

Supporting data:
{supportJson}
";
    }

    private static string InferExecutiveFocus(string? userText)
    {
        var t = (userText ?? string.Empty).ToLowerInvariant();

        if (t.Contains("customer"))
            return "customer";

        if (t.Contains("product") || t.Contains("category") || t.Contains("inventory"))
            return "product";

        if (t.Contains("employee") || t.Contains("headcount") || t.Contains("workforce") || t.Contains("department") || t.Contains("staff"))
            return "workforce";

        if (t.Contains("sales") || t.Contains("revenue") || t.Contains("order") || t.Contains("territory"))
            return "sales";

        return "business";
    }

    private static string TruncateForExecutiveInput(string text, int maxChars = 12000)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    // ✅ Decision hook: attach candidate (no text mutation)

}
