using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text;

namespace Ai.AgentFramwork.Massar.Web.Agents;

public sealed class AgentCallerTool
{
    private readonly IAgentRegistry _registry;
    private readonly Func<IReadOnlyList<ChatMessage>> _historyProvider;
    private readonly Action<string>? _onRoute;

    public AgentCallerTool(
        IAgentRegistry registry,
        Func<IReadOnlyList<ChatMessage>> historyProvider,
        Action<string>? onRoute = null)
    {
        _registry = registry;
        _historyProvider = historyProvider;
        _onRoute = onRoute;
    }





    public sealed record AgentCallResult(string AgentName, string Text);

    [Description("Calls a specialized agent and returns its final response.")]
    public async Task<AgentCallResult> CallAgentAsync(
    [Description("The agent name to call.")] string agentName,
    [Description("The user's latest message text.")] string userMessage,
    [Description("Optional: comma-separated filename filter for document scenarios.")] string? filenameFilterCsv = null,
    [Description("How many recent conversation messages to include.")] int historyWindowMessages = 10,
    CancellationToken cancellationToken = default)
    {
        _onRoute?.Invoke(agentName);

        var agent = _registry.GetRequired(agentName);

        // IMPORTANT: Only send a SAFE text-only transcript to avoid tool_call sequencing errors
        var history = _historyProvider?.Invoke() ?? Array.Empty<ChatMessage>();
        var safeTranscript = ToSafeTextOnlyMessages(history, historyWindowMessages);

        var sys = "You are a specialized agent. Use the conversation transcript to answer the user's latest request.";
        if (!string.IsNullOrWhiteSpace(filenameFilterCsv))
            sys += $"\nIf searching documents, prefer these files: {filenameFilterCsv}";

        var messages = new List<ChatMessage> { new(ChatRole.System, sys) };
        messages.AddRange(safeTranscript);

        // Ensure the latest user message is present (often already present)
        if (messages.LastOrDefault()?.Role != ChatRole.User)
            messages.Add(new ChatMessage(ChatRole.User, new[] { new TextContent(userMessage) }));

        var sb = new StringBuilder();

        Console.WriteLine(">>> Starting RunStreamingAsync");

        await foreach (var update in agent.RunStreamingAsync(messages: messages, cancellationToken: cancellationToken))
        {
            var appended = false;

            if (update.Contents is not null && update.Contents.Count > 0)
            {
                foreach (var tc in update.Contents.OfType<TextContent>())
                {
                    if (!string.IsNullOrEmpty(tc.Text))
                    {
                        sb.Append(tc.Text);
                        appended = true;
                    }
                }
            }

            // Only fallback to update.Text if no TextContent was appended
            if (!appended && !string.IsNullOrEmpty(update.Text))
            {
                sb.Append(update.Text);
            }

            Console.WriteLine($"UPDATE: {update.GetType().FullName} TextLen={update.Text?.Length ?? 0} Contents={update.Contents?.Count ?? 0}");
        }

        var finalText = sb.ToString();

        // Safety: never return empty (prevents NO_RESPONSE_FROM_AGENT confusion)
        if (string.IsNullOrWhiteSpace(finalText))
            finalText = "NO_RESPONSE_FROM_AGENT";

        return new AgentCallResult(agentName, finalText);
    }
    //public async Task<AgentCallResult> CallAgentAsync(
    //    [Description("The agent name to call.")] string agentName,
    //    [Description("The user's latest message text.")] string userMessage,
    //    [Description("Optional: comma-separated filename filter for document scenarios.")] string? filenameFilterCsv = null,
    //    [Description("How many recent conversation messages to include.")] int historyWindowMessages = 10,
    //    CancellationToken cancellationToken = default)
    //{
    //    _onRoute?.Invoke(agentName);

    //    var agent = _registry.GetRequired(agentName);

    //    // IMPORTANT: Only send a SAFE text-only transcript to avoid tool_call sequencing errors
    //    var history = _historyProvider?.Invoke() ?? Array.Empty<ChatMessage>();
    //    var safeTranscript = ToSafeTextOnlyMessages(history, historyWindowMessages);

    //    var sys = "You are a specialized agent. Use the conversation transcript to answer the user's latest request.";
    //    if (!string.IsNullOrWhiteSpace(filenameFilterCsv))
    //        sys += $"\nIf searching documents, prefer these files: {filenameFilterCsv}";

    //    var messages = new List<ChatMessage> { new(ChatRole.System, sys) };
    //    messages.AddRange(safeTranscript);

    //    // Ensure the latest user message is present (often already present)
    //    if (messages.LastOrDefault()?.Role != ChatRole.User)
    //        messages.Add(new ChatMessage(ChatRole.User, new[] { new TextContent(userMessage) }));

    //    var sb = new StringBuilder();

    //    Console.WriteLine(">>> Starting RunStreamingAsync");

    //    // NOTE: In your SDK, update.Text can be empty while update.Contents contains TextContent.
    //    await foreach (var update in agent.RunStreamingAsync(messages: messages, cancellationToken: cancellationToken))
    //    {
    //        Console.WriteLine($"UPDATE: {update.GetType().FullName} TextLen={update.Text?.Length ?? 0} Contents={update.Contents?.Count ?? 0}");

    //        if (update.Contents != null)
    //            foreach (var c in update.Contents)
    //                Console.WriteLine($"  - content: {c.GetType().FullName}");
    //    }

    //    return new AgentCallResult(agentName, sb.ToString());
    //}

    private static List<ChatMessage> ToSafeTextOnlyMessages(IEnumerable<ChatMessage> source, int takeLast)
    {
        var window = source as IList<ChatMessage> ?? source.ToList();

        if (takeLast > 0 && window.Count > takeLast)
            window = window.Skip(window.Count - takeLast).ToList();

        var safe = new List<ChatMessage>();

        foreach (var m in window)
        {
            if (m.Role != ChatRole.User && m.Role != ChatRole.Assistant)
                continue;

            var text = string.Concat(
                m.Contents
                 .OfType<TextContent>()
                 .Select(t => t.Text));

            if (string.IsNullOrWhiteSpace(text))
                continue;

            safe.Add(new ChatMessage(m.Role, new[] { new TextContent(text) }));
        }

        return safe;
    }
}