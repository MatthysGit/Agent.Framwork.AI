using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Ai.AgentFramwork.Massar.Web.Tests;

public sealed class RouterAgentHardGuardTests
{
    private static RouterAgent CreateSut(params ChatMessage[] history)
        => new(chat: null!, historyProvider: () => history);

    public static TheoryData<string, string> HardGuardCases => new()
    {
        { "Please review the document and add comments throughout the file.", ChatAgentFactory.DocumentEditAgentName },
        { "Analyze this Excel spreadsheet and give me KPI insights.", ChatAgentFactory.ExcelAnalyticsAgentName },
        { "Give me an executive summary of company performance this month.", ChatAgentFactory.ExecutiveInsightAgentName },
        { "What does the data suggest about the drivers of performance?", ChatAgentFactory.DataIntelligenceAgentName },
        { "Forecast monthly sales for the next 6 months.", ChatAgentFactory.ForecastingAgentName },
        { "Find revenue anomalies and unusual spikes by month.", ChatAgentFactory.AnomalyDetectionAgentName },
        { "Give me a sales breakdown by region and category.", ChatAgentFactory.DataSegmentationAgentName },
        { "What happens if we increase prices by 10%?", ChatAgentFactory.WhatIfSimulationAgentName },
        { "What is included in the survival kit policy document?", ChatAgentFactory.DocumentSearchAgentName },
    };

    [Theory]
    [MemberData(nameof(HardGuardCases))]
    public async Task RouteAsync_Should_route_known_hard_guard_requests(string message, string expectedAgent)
    {
        var sut = CreateSut();

        var result = await sut.RouteAsync(message);

        result.Mode.Should().Be("agent");
        result.Agent.Should().Be(expectedAgent);
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RouteAsync_Should_prioritize_document_edit_over_excel_analytics_when_both_signals_exist()
    {
        var sut = CreateSut();

        var result = await sut.RouteAsync("Review the attached Excel spreadsheet and add comments to the document.");

        result.Agent.Should().Be(ChatAgentFactory.DocumentEditAgentName);
        result.Reason.Should().ContainEquivalentOf("document edit intent");
    }

    [Fact]
    public async Task RouteAsync_Should_prioritize_excel_analytics_over_document_content_when_attachment_link_is_present()
    {
        var sut = CreateSut();

        var result = await sut.RouteAsync("Analyze /api/chat/attachments/123 sales spreadsheet and build a dashboard.");

        result.Agent.Should().Be(ChatAgentFactory.ExcelAnalyticsAgentName);
    }

    [Fact]
    public async Task RouteAsync_Should_route_document_search_for_document_content_question()
    {
        var sut = CreateSut();

        var result = await sut.RouteAsync("What does the policy document say?");

        result.Mode.Should().Be("agent");
        result.Agent.Should().Be(ChatAgentFactory.DocumentSearchAgentName);
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }
}
