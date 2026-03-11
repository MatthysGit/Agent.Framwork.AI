using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.Services.Chat;

namespace Ai.AgentFramwork.Massar.Web.Tests;

public sealed record AgentPromptCase(
    string AgentName,
    string Prompt,
    string[] RequiredReasonTerms);

internal static class AgentPromptCatalog
{
    public static IReadOnlyList<AgentPromptCase> LongFormCases { get; } =
    [
        new(
            ChatAgentFactory.AnomalyDetectionAgentName,
            "Analyze monthly internet sales for the last 24 months and identify any anomalies in revenue, order count, or average order value. Explain the likely causes of each anomaly.",
            ["anomaly", "detect"]),
        new(
            ChatAgentFactory.DataExplorerAgentName,
            "Explore reseller sales performance by country, region, and product category. Show the top patterns, strongest markets, weakest markets, and any notable trends.",
            ["sql", "database", "exploration"]),
        new(
            ChatAgentFactory.DataIntelligenceAgentName,
            "Provide a business intelligence summary of sales performance across products, territories, and customer segments. Highlight the most important insights and recommended actions for leadership.",
            ["data intelligence"]),
        new(
            ChatAgentFactory.DataSegmentationAgentName,
            "Segment customers based on total spend, order frequency, and recency. Identify the most valuable customer segments and describe each segment.",
            ["segmentation"]),
        new(
            ChatAgentFactory.ExecutiveInsightAgentName,
            "Give me an executive summary of sales performance for the current year versus last year, including revenue, margin, top-performing regions, underperforming categories, and strategic recommendations.",
            ["executive"]),
        new(
            ChatAgentFactory.ForecastingAgentName,
            "Forecast the next 6 months of internet sales revenue based on historical monthly sales. Include confidence levels and explain the projected trend.",
            ["forecast"]),
        new(
            ChatAgentFactory.SqlAgentName,
            "Show me the top 10 sales territories by SalesYTD, including territory name, country region code, group, SalesYTD, and SalesLastYear.",
            ["sql", "database"]),
        new(
            ChatAgentFactory.WhatIfSimulationAgentName,
            "What would be the impact on total revenue and gross profit if bike sales volume increased by 12% next quarter while average discount rate increased by 3%?",
            ["what-if", "simulation"])
    ];

    public static IReadOnlyList<AgentPromptCase> QuickPromptCases { get; } =
    [
        new(ChatAgentFactory.AnomalyDetectionAgentName,
            "Find anomalies in monthly internet sales over the last 24 months and explain them.",
            ["anomaly"]),
        new(ChatAgentFactory.DataExplorerAgentName,
            "Explore reseller sales by geography and category and show the main patterns.",
            ["sql", "database", "exploration"]),
        new(ChatAgentFactory.DataIntelligenceAgentName,
            "Generate the most important business insights from sales, products, and customer data.",
            ["data intelligence"]),
        new(ChatAgentFactory.DataSegmentationAgentName,
            "Segment customers by spend, frequency, and recency and describe each segment.",
            ["segmentation"]),
        new(ChatAgentFactory.ExecutiveInsightAgentName,
            "Give an executive summary of this year vs last year sales performance with actions.",
            ["executive"]),
        new(ChatAgentFactory.ForecastingAgentName,
            "Forecast the next 6 months of internet sales revenue and explain the trend.",
            ["forecast"]),
        new(ChatAgentFactory.SqlAgentName,
            "Show the top 10 sales territories by SalesYTD.",
            ["sql", "database"]),
        new(ChatAgentFactory.WhatIfSimulationAgentName,
            "Simulate a 12% increase in bike sales volume with a 3% increase in discount rate next quarter.",
            ["what-if", "simulation"])
    ];
}
