using Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;
using FluentAssertions;
using Xunit;

namespace Ai.AgentFramwork.Massar.Web.Tests;

public sealed class ChatPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_Should_block_individual_compensation_questions_for_non_privileged_users()
    {
        var sut = new ChatPipeline(
            router: null!,
            caller: null!,
            chartTools: null!,
            docSearchTool: null!,
            docEditTool: null!,
            getConversationId: () => Guid.NewGuid(),
            canViewCompensationAsync: () => Task.FromResult(false),
            isPrivilegedAsync: () => Task.FromResult(false),
            decisionTracker: null!);

        var result = await sut.ExecuteAsync("What is Ahmed's salary?");

        result.RoutedAgent.Should().Be("PolicyGuard");
        result.RouterReason.Should().Be("Blocked: individual compensation request");
        result.Text.Should().NotBeNullOrWhiteSpace();
        result.Text.ToLowerInvariant().Should().Contain("salary/compensation");
    }
}