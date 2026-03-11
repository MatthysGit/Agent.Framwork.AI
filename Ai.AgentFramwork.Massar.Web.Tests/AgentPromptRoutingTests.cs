using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Ai.AgentFramwork.Massar.Web.Tests;

public sealed class AgentPromptRoutingTests
{
    private static RouterAgent CreateSut(params ChatMessage[] history)
        => new(chat: null!, historyProvider: () => history);

    public static IEnumerable<object[]> LongFormCases()
        => AgentPromptCatalog.LongFormCases.Select(x => new object[] { x });

    public static IEnumerable<object[]> QuickPromptCases()
        => AgentPromptCatalog.QuickPromptCases.Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(LongFormCases))]
    public async Task RouteAsync_Should_route_long_form_uat_prompts_to_expected_agents(AgentPromptCase testCase)
    {
        var sut = CreateSut();

        var result = await sut.RouteAsync(testCase.Prompt);

        result.Mode.Should().Be("agent");
        result.Agent.Should().Be(testCase.AgentName);
        result.Reason.Should().NotBeNullOrWhiteSpace();

        foreach (var term in testCase.RequiredReasonTerms)
        {
            result.Reason.Should().ContainEquivalentOf(term);
        }
    }

    [Theory]
    [MemberData(nameof(QuickPromptCases))]
    public async Task RouteAsync_Should_route_quick_prompts_to_expected_agents(AgentPromptCase testCase)
    {
        var sut = CreateSut();

        var result = await sut.RouteAsync(testCase.Prompt);

        result.Mode.Should().Be("agent");
        result.Agent.Should().Be(testCase.AgentName);
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RouteAsync_Should_route_sql_uat_prompt_to_data_explorer_when_prompt_is_open_exploration()
    {
        var sut = CreateSut();

        var result = await sut.RouteAsync(
            "Explore reseller sales performance by country, region, and product category. Show the top patterns, strongest markets, weakest markets, and any notable trends.");

        result.Agent.Should().Be(ChatAgentFactory.DataExplorerAgentName);
        result.Reason.Should().ContainEquivalentOf("open exploratory analysis");
    }

    [Fact]
    public async Task RouteAsync_Should_route_ranked_sales_territory_prompt_to_sql_agent()
    {
        var sut = CreateSut();

        var result = await sut.RouteAsync(
            "Show me the top 10 sales territories by SalesYTD, including territory name, country region code, group, SalesYTD, and SalesLastYear.");

        result.Agent.Should().Be(ChatAgentFactory.SqlAgentName);
        result.Reason.Should().ContainEquivalentOf("open exploratory analysis");
    }
}
