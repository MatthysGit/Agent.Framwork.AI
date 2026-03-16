// File: Services/Chat/ImproveConversationService.cs
#nullable enable
using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.DTO;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

#pragma warning disable OPENAI001

/// <summary>
/// ImproveConversationService
/// - Adds HTTP 429 retry/backoff (TPM/RPM rate-limit resiliency)
/// - Aggressively trims/sanitizes chat transcript to reduce token usage
///
/// DI NOTE:
/// We do NOT inject OpenAI.Chat.ChatClient directly (to avoid DI resolution issues).
/// Instead we inject your existing runtime model factory/selector and create a ChatClient per call.
/// </summary>
public sealed class ImproveConversationService : IImproveConversationService
{
    // Use a dedicated name so you can map this separately in IAgentModelSelector if you want.
    public const string ImproveAgentName = "ImproveConversationService";

    private readonly IChatClientFactory _chatClientFactory;
    private readonly IAgentModelSelector _modelSelector;
    private readonly ChatService _chatService;
    private readonly IAiUsageLogger? _aiUsageLogger;
    private readonly IAiUsageContextAccessor? _aiUsageContextAccessor;

    public ImproveConversationService(
        IChatClientFactory chatClientFactory,
        IAgentModelSelector modelSelector,
        ChatService chatService,
        IAiUsageLogger? aiUsageLogger = null,
        IAiUsageContextAccessor? aiUsageContextAccessor = null)
    {
        _chatClientFactory = chatClientFactory;
        _modelSelector = modelSelector;
        _chatService = chatService;
        _aiUsageLogger = aiUsageLogger;
        _aiUsageContextAccessor = aiUsageContextAccessor;
    }

    public async Task<ImproveConversationResultDto> ImproveAsync(
        string userId,
        Guid conversationId,
        ImproveOptions? options,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId is required.", nameof(userId));

        if (conversationId == Guid.Empty)
            throw new ArgumentException("ConversationId is required.", nameof(conversationId));

        // Pick model at runtime.
        // Prefer a dedicated mapping if present, else fall back to your normal LLM agent mapping.
        var modelKey = SafeGetModelKey(ImproveAgentName)
                       ?? SafeGetModelKey(ChatAgentFactory.LlmChatAgentName)
                       ?? SafeGetModelKey(ChatAgentFactory.OrchestratorAgentName)
                       ?? "gpt-4.1-mini"; // last-resort fallback

        ChatClient chat = _chatClientFactory.Create(modelKey);

        // Build a safe, compact transcript to keep tokens low.
        // Keep this conservative to avoid TPM spikes.
        const int takeLast = 24;
        const int maxCharsPerMessage = 3000;

        var transcript = BuildTrimmedTranscript(_chatService.Messages, takeLast, maxCharsPerMessage);

        var system =
            "You are an assistant that writes concise, professional email summaries. " +
            "Use bullet points, clear next steps, and keep it skimmable.";

        var user = $"""
Conversation transcript:
{transcript}

Task:
Create an email-ready summary with:
- Key points
- Decisions (if any)
- Next steps / action items

Return plain Markdown (no JSON). Keep it professional.
""";

        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            new SystemChatMessage(system),
            new UserChatMessage(user)
        };

        // ✅ Rate limit resiliency
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await OpenAiRateLimitHelpers.Retry429Async(
            innerCt => chat.CompleteChatAsync(messages, cancellationToken: innerCt),
            ct);
        sw.Stop();

        var md = resp.Value?.Content?.FirstOrDefault()?.Text?.Trim() ?? "";

        if (_aiUsageLogger is not null)
        {
            var ctx = _aiUsageContextAccessor?.GetCurrent();
            _aiUsageContextAccessor?.SetAgent(ImproveAgentName, modelKey);
            var snapshot = AiUsageReflection.ExtractSnapshot(resp);
            var promptText = string.Join("\n", messages.Select(m => string.Concat(m.Content.Select(c => c.Text ?? string.Empty))));
            var estimatedPrompt = snapshot.PromptTokens ?? AiTokenEstimator.EstimateTextTokens(promptText);
            var estimatedCompletion = snapshot.CompletionTokens ?? AiTokenEstimator.EstimateTextTokens(md);

            await _aiUsageLogger.LogAsync(new AiUsageLogRequest(
                AgentName: ImproveAgentName,
                ModelName: modelKey,
                UserId: userId,
                ConversationId: conversationId,
                MessageId: null,
                PromptTokens: snapshot.PromptTokens ?? estimatedPrompt,
                CompletionTokens: snapshot.CompletionTokens ?? estimatedCompletion,
                TotalTokens: snapshot.TotalTokens ?? (estimatedPrompt + estimatedCompletion),
                CachedInputTokens: snapshot.CachedInputTokens,
                ReasoningTokens: snapshot.ReasoningTokens,
                RequestId: snapshot.RequestId,
                ClientRequestId: ctx?.ClientRequestId,
                LatencyMs: (int)sw.ElapsedMilliseconds,
                Succeeded: true,
                ErrorMessage: null), ct);
        }

        if (string.IsNullOrWhiteSpace(md))
            md = "Hi team,\n\nHere is a summary of our conversation.\n";

        // ✅ Build the DTO via constructor-first (records often have init-only props)
        // so that ImproveConversationDialog can read the correct fields.
        var subject = ImproveDtoBuilder.DeriveSubject(md) ?? "Conversation Summary";
        return ImproveDtoBuilder.CreateResult(subject, md);
    }

    private string? SafeGetModelKey(string agentName)
    {
        try
        {
            var key = _modelSelector.GetModelForAgent(agentName);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch
        {
            return null;
        }
    }

    // -------------------------
    // Token trimming helpers
    // -------------------------

    // Supports both legacy <!--TOOL:...--> and markdown comment [//]: # (TOOL:...).
    private static readonly Regex ToolMarkerRegex =
        new(@"(?:<!--\s*TOOL:(?<type>[^:]+):(?<b64>[^>]+?)\s*-->)|(?:\[\s*\/\/\s*\]\s*:\s*#\s*\(\s*TOOL:(?<type2>[^:]+):(?<b642>[^)]+?)\s*\)\s*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static string BuildTrimmedTranscript(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        int takeLast,
        int maxCharsPerMessage)
    {
        var window = messages
            .Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
            .ToList();

        if (takeLast > 0 && window.Count > takeLast)
            window = window.Skip(window.Count - takeLast).ToList();

        var sb = new StringBuilder(16 * 1024);

        foreach (var m in window)
        {
            var raw = string.Concat(m.Contents.OfType<TextContent>().Select(t => t.Text));
            var clean = StripToolPayloads(raw);
            clean = TrimTo(clean, maxCharsPerMessage);

            if (string.IsNullOrWhiteSpace(clean))
                continue;

            sb.Append(m.Role == ChatRole.User ? "User: " : "Assistant: ");
            sb.AppendLine(clean);
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private static string StripToolPayloads(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        // Remove hidden tool markers that often contain base64 blobs (huge tokens)
        s = ToolMarkerRegex.Replace(s, "");

        // Remove any remaining long base64-ish runs (defensive)
        s = Regex.Replace(s, @"[A-Za-z0-9+/]{200,}={0,2}", "[omitted]", RegexOptions.Singleline);

        return s.Trim();
    }

    private static string TrimTo(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (maxChars <= 0) return "";
        return s.Length <= maxChars ? s : s[..maxChars] + "…";
    }

    // -------------------------
    // DTO builder (reflection-safe)
    // -------------------------

    private static class ImproveDtoBuilder
    {
        public static string? DeriveSubject(string bodyMarkdown)
        {
            if (string.IsNullOrWhiteSpace(bodyMarkdown)) return null;
            foreach (var line in bodyMarkdown.Replace("\r\n", "\n").Split('\n'))
            {
                var t = line.Trim();
                if (string.IsNullOrWhiteSpace(t)) continue;
                t = t.TrimStart('#', '*', '-', ' ');
                return t.Length > 90 ? t[..90] : t;
            }
            return null;
        }

        public static ImproveConversationResultDto CreateResult(string subject, string bodyMarkdown)
        {
            var t = typeof(ImproveConversationResultDto);

            // Prefer ctor shapes (records often have init-only props)
            foreach (var ctor in t.GetConstructors().OrderByDescending(c => c.GetParameters().Length))
            {
                var ps = ctor.GetParameters();

                // (string threadSummary, decisions, actionItems, openQuestions, artifacts)
                if (ps.Length == 5 && ps[0].ParameterType == typeof(string))
                {
                    var args = new object?[]
                    {
                        bodyMarkdown,
                        CreateList(ps[1].ParameterType),
                        CreateList(ps[2].ParameterType),
                        CreateList(ps[3].ParameterType),
                        CreateArtifacts(ps[4].ParameterType, subject, bodyMarkdown)
                    };
                    try { return (ImproveConversationResultDto)ctor.Invoke(args); }
                    catch { }
                }

                // (string threadSummary, artifacts)
                if (ps.Length == 2 && ps[0].ParameterType == typeof(string))
                {
                    var args = new object?[] { bodyMarkdown, CreateArtifacts(ps[1].ParameterType, subject, bodyMarkdown) };
                    try { return (ImproveConversationResultDto)ctor.Invoke(args); }
                    catch { }
                }
            }

            // Fallback: parameterless then set what we can
            var emptyCtor = t.GetConstructor(Type.EmptyTypes);
            if (emptyCtor is not null)
            {
                var inst = (ImproveConversationResultDto)emptyCtor.Invoke(null);
                SetString(inst, "ThreadSummary", bodyMarkdown);
                SetString(inst, "EmailSubject", subject);
                SetString(inst, "EmailBodyMarkdown", bodyMarkdown);
                var artifacts = GetOrCreateArtifacts(inst, subject, bodyMarkdown);
                _ = artifacts;
                return inst;
            }

            // Last resort
            var fallback = (ImproveConversationResultDto)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
            return fallback;
        }

        private static object? GetOrCreateArtifacts(ImproveConversationResultDto dto, string subject, string bodyMarkdown)
        {
            try
            {
                var p = dto.GetType().GetProperty("Artifacts", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p is null) return null;

                var existing = p.GetValue(dto);
                if (existing is not null)
                {
                    SetEmailOnArtifacts(existing, subject, bodyMarkdown);
                    return existing;
                }

                if (!p.CanWrite) return null;

                var created = CreateArtifacts(p.PropertyType, subject, bodyMarkdown);
                if (created is null) return null;

                p.SetValue(dto, created);
                return created;
            }
            catch
            {
                return null;
            }
        }

        public static void SetString(object target, string propName, string value)
        {
            try
            {
                var p = target.GetType().GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (p is null) return;
                if (!p.CanWrite) return;
                if (p.PropertyType != typeof(string)) return;
                p.SetValue(target, value);
            }
            catch { }
        }

        private static object? CreateArtifacts(Type t, string subject, string bodyMarkdown)
        {
            try
            {
                // paramless
                var ctor0 = t.GetConstructor(Type.EmptyTypes);
                if (ctor0 is not null)
                {
                    var a = ctor0.Invoke(null);
                    if (a is not null) SetEmailOnArtifacts(a, subject, bodyMarkdown);
                    return a;
                }

                // common: (Email, Spec)
                var ctor2 = t.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 2);
                if (ctor2 is not null)
                {
                    var ps = ctor2.GetParameters();
                    var emailObj = CreateEmailDto(ps[0].ParameterType, subject, bodyMarkdown);
                    var specObj = CreateSpecDto(ps[1].ParameterType);
                    var a = ctor2.Invoke(new[] { emailObj, specObj });
                    if (a is not null) SetEmailOnArtifacts(a, subject, bodyMarkdown);
                    return a;
                }

                var u = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
                SetEmailOnArtifacts(u, subject, bodyMarkdown);
                return u;
            }
            catch { return null; }
        }

        private static void SetEmailOnArtifacts(object artifacts, string subject, string bodyMarkdown)
        {
            // If there is Email property, set it
            var pEmail = artifacts.GetType().GetProperty("Email", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pEmail is not null && pEmail.CanWrite)
            {
                try
                {
                    var emailObj = CreateEmailDto(pEmail.PropertyType, subject, bodyMarkdown);
                    if (emailObj is not null)
                        pEmail.SetValue(artifacts, emailObj);
                }
                catch { }
            }

            SetString(artifacts, "EmailSubject", subject);
            SetString(artifacts, "Subject", subject);
            SetString(artifacts, "EmailBodyMarkdown", bodyMarkdown);
            SetString(artifacts, "BodyMarkdown", bodyMarkdown);
        }

        private static object? CreateEmailDto(Type emailType, string subject, string bodyMarkdown)
        {
            try
            {
                foreach (var ctor in emailType.GetConstructors().OrderByDescending(c => c.GetParameters().Length))
                {
                    var ps = ctor.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                        return ctor.Invoke(new object[] { subject, bodyMarkdown });
                    if (ps.Length == 0)
                    {
                        var inst = ctor.Invoke(null);
                        if (inst is not null)
                        {
                            SetString(inst, "Subject", subject);
                            SetString(inst, "BodyMarkdown", bodyMarkdown);
                            SetString(inst, "EmailBodyMarkdown", bodyMarkdown);
                        }
                        return inst;
                    }
                }

                var u = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(emailType);
                SetString(u, "Subject", subject);
                SetString(u, "BodyMarkdown", bodyMarkdown);
                SetString(u, "EmailBodyMarkdown", bodyMarkdown);
                return u;
            }
            catch { return null; }
        }

        private static object? CreateSpecDto(Type specType)
        {
            try
            {
                var c0 = specType.GetConstructor(Type.EmptyTypes);
                if (c0 is not null) return c0.Invoke(null);
                var c1 = specType.GetConstructor(new[] { typeof(string) });
                if (c1 is not null) return c1.Invoke(new object[] { string.Empty });
                return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(specType);
            }
            catch { return null; }
        }

        private static object? CreateList(Type listType)
        {
            try
            {
                var ctor = listType.GetConstructor(Type.EmptyTypes);
                if (ctor is not null) return ctor.Invoke(null);

                if (listType.IsGenericType)
                {
                    var gen = listType.GetGenericArguments()[0];
                    var concrete = typeof(List<>).MakeGenericType(gen);
                    return Activator.CreateInstance(concrete);
                }
            }
            catch { }

            return null;
        }
    }
}
