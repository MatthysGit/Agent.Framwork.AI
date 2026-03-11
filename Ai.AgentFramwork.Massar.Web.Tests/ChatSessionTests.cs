using Ai.AgentFramwork.Massar.Web.Services.Chat;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Ai.AgentFramwork.Massar.Web.Tests;

public sealed class ChatSessionTests
{
    [Fact]
    public void CancelAnyCurrentResponse_Should_append_partial_message_when_requested()
    {
        var session = new ChatSession();
        var partial = new ChatMessage(ChatRole.Assistant, [new TextContent("Partial response")]);

        session.SetStreamingMessage(partial);
        session.CancelAnyCurrentResponse(addPartialToTranscript: true);

        session.Messages.Should().ContainSingle();
        string.Concat(session.Messages[0].Contents.OfType<TextContent>().Select(t => t.Text))
            .Should().Be("Partial response");
        session.CurrentResponseMessage.Should().BeNull();
    }

    [Fact]
    public void StartNewChat_Should_clear_messages_streaming_state_and_attachments()
    {
        var session = new ChatSession();
        session.SetActiveConversation(Guid.NewGuid());
        session.AddMessage(new ChatMessage(ChatRole.User, [new TextContent("hello")]));
        session.SetStreamingMessage(new ChatMessage(ChatRole.Assistant, [new TextContent("working")]));
        session.TrackAssistantAttachment(new ChatAttachmentInfo(Guid.NewGuid(), "report.csv", "text/csv", "/api/chat/attachments/1", 128));

        session.StartNewChat();

        session.ActiveConversationId.Should().BeNull();
        session.Messages.Should().BeEmpty();
        session.CreatedAssistantAttachments.Should().BeEmpty();
        session.CurrentResponseMessage.Should().BeNull();
    }
}