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

        // Ensure the latest user message is present
        if (messages.LastOrDefault()?.Role != ChatRole.User)
            messages.Add(new ChatMessage(ChatRole.User, new[] { new TextContent(userMessage) }));

        var sb = new StringBuilder();

        // Streaming updates can be delta OR cumulative depending on provider.
        // This deduper prevents repeated blocks and double-appends.
        var lastFull = "";

        await foreach (var update in agent.RunStreamingAsync(messages: messages, cancellationToken: cancellationToken))
        {
            var fromContents = ExtractTextFromContents(update.Contents);
            if (!string.IsNullOrWhiteSpace(fromContents))
            {
                AppendDelta(sb, ref lastFull, fromContents);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(update.Text))
            {
                AppendDelta(sb, ref lastFull, update.Text);
            }
        }

        var finalText = sb.ToString();
        if (string.IsNullOrWhiteSpace(finalText))
            finalText = "NO_RESPONSE_FROM_AGENT";

        return new AgentCallResult(agentName, finalText);
    }

    private static string ExtractTextFromContents(IReadOnlyList<AIContent>? contents)
    {
        if (contents is null || contents.Count == 0) return "";

        var sb = new StringBuilder();
        foreach (var tc in contents.OfType<TextContent>())
        {
            if (!string.IsNullOrEmpty(tc.Text))
                sb.Append(tc.Text);
        }
        return sb.ToString();
    }

    private static void AppendDelta(StringBuilder sb, ref string lastFull, string incoming)
    {
        if (!string.IsNullOrEmpty(lastFull) && incoming.StartsWith(lastFull, StringComparison.Ordinal))
        {
            sb.Append(incoming.AsSpan(lastFull.Length));
            lastFull = incoming;
            return;
        }

        if (incoming == lastFull) return;

        var idx = !string.IsNullOrEmpty(lastFull) ? incoming.IndexOf(lastFull, StringComparison.Ordinal) : -1;
        if (idx >= 0)
        {
            var suffixStart = idx + lastFull.Length;
            if (suffixStart < incoming.Length)
                sb.Append(incoming.AsSpan(suffixStart));
            lastFull = incoming;
            return;
        }

        sb.Append(incoming);
        lastFull = incoming;
    }

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