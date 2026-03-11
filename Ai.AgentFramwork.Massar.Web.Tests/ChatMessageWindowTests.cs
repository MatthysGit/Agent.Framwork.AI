using FluentAssertions;
using Microsoft.Extensions.AI;
using System.Reflection;
using Xunit;

namespace Ai.AgentFramwork.Massar.Web.Tests;

public sealed class ChatMessageWindowTests
{
    [Fact]
    public void ToSafeTextOnlyMessages_Should_keep_only_user_and_assistant_text_messages()
    {
        var source = new List<ChatMessage>
        {
            new(ChatRole.System, [new TextContent("system")]),
            new(ChatRole.User, [new TextContent("user 1")]),
            new(ChatRole.Assistant, [new TextContent("assistant 1")]),
            new(ChatRole.Tool, [new TextContent("tool output")]),
            new(ChatRole.User, [new TextContent("user 2")])
        };

        var result = InvokeToSafeTextOnlyMessages(source, takeLast: 10);

        result.Should().HaveCount(3);
        result.Select(m => m.Role).Should().ContainInOrder(ChatRole.User, ChatRole.Assistant, ChatRole.User);
        result.Select(m => string.Concat(m.Contents.OfType<TextContent>().Select(t => t.Text)))
            .Should().ContainInOrder("user 1", "assistant 1", "user 2");
    }

    [Fact]
    public void ToSafeTextOnlyMessages_Should_trim_to_last_requested_window_before_filtering_text()
    {
        var source = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextContent("u1")]),
            new(ChatRole.Assistant, [new TextContent("a1")]),
            new(ChatRole.User, [new TextContent("u2")]),
            new(ChatRole.Assistant, [new TextContent("a2")])
        };

        var result = InvokeToSafeTextOnlyMessages(source, takeLast: 2);

        result.Should().HaveCount(2);
        result.Select(m => string.Concat(m.Contents.OfType<TextContent>().Select(t => t.Text)))
            .Should().ContainInOrder("u2", "a2");
    }

    private static IReadOnlyList<ChatMessage> InvokeToSafeTextOnlyMessages(
        IReadOnlyList<ChatMessage> source,
        int takeLast)
    {
        var type = FindChatMessageWindowType();
        type.Should().NotBeNull("ChatMessageWindow should exist in the main project");

        var method = type!.GetMethod(
            "ToSafeTextOnlyMessages",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull("ChatMessageWindow.ToSafeTextOnlyMessages should exist");

        var result = method!.Invoke(null, [source, takeLast]);
        result.Should().NotBeNull();

        return result.Should().BeAssignableTo<IReadOnlyList<ChatMessage>>().Subject;
    }

    private static Type? FindChatMessageWindowType()
    {
        var webAssembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Ai.AgentFramwork.Massar.Web");

        webAssembly.Should().NotBeNull("Ai.AgentFramwork.Massar.Web assembly should be loaded by the test project");

        return webAssembly!
            .GetTypes()
            .FirstOrDefault(t => t.Name == "ChatMessageWindow");
    }
}
