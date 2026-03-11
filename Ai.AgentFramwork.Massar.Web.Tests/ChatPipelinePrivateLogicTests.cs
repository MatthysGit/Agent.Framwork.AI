using Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;
using FluentAssertions;
using System.Reflection;
using Xunit;

namespace Ai.AgentFramwork.Massar.Web.Tests;

public sealed class ChatPipelinePrivateLogicTests
{
    [Theory]
    [InlineData("Please comment on the proposal", "add comments")]
    [InlineData("Annotate this policy", "annotate with comments")]
    [InlineData("Highlight the issues in this file", "highlight issues and add comments")]
    [InlineData("Proofread this contract", "proofread and add comments")]
    [InlineData("Revise this document", "make suggested edits and add comments")]
    [InlineData("Edit this agreement", "make suggested edits and add comments")]
    [InlineData("Review this doc", "add comments")]
    public void InferEditInstruction_Should_map_expected_instruction(string input, string expected)
    {
        var result = InvokePrivateStatic<string>("InferEditInstruction", input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Could you clarify which month you mean?", true)]
    [InlineData("Please specify the department.", true)]
    [InlineData("I need more context before I can continue.", true)]
    [InlineData("Here is the summary you requested.", false)]
    public void LooksLikeClarifyingQuestion_Should_classify_text_correctly(string text, bool expected)
    {
        var result = InvokePrivateStatic<bool>("LooksLikeClarifyingQuestion", text);

        result.Should().Be(expected);
    }

    [Fact]
    public void ExtractFirstJsonObject_Should_return_first_json_payload_from_mixed_text()
    {
        const string input = "Some prefix text { \"rows\": [ { \"Period\": \"2025-01-01\", \"Value\": 100 } ] } trailing text";

        var result = InvokePrivateStatic<string?>("ExtractFirstJsonObject", input);

        result.Should().NotBeNull();
        result.Should().Contain("\"rows\"");
        result.Should().StartWith("{");
        result.Should().EndWith("}");
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[]? args)
    {
        var method = typeof(ChatPipeline).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull($"private static method {methodName} should exist");

        var result = method!.Invoke(null, args);
        result.Should().NotBeNull();

        return (T)result!;
    }
}
