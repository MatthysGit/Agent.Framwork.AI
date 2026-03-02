using Ai.AgentFramwork.Massar.Web.Agents;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.DecisionTracking;

public interface IDecisionTrackerService
{
    Task<DecisionDetection?> DetectAsync(
        string userText,
        string assistantText,
        Guid? conversationId,
        CancellationToken ct = default);

    Task<DecisionDraft?> BuildDraftAsync(
        string userText,
        string assistantText,
        DecisionDetection detection,
        Guid? conversationId,
        string? ownerUserId,
        CancellationToken ct = default);
}

public sealed class DecisionTrackerService : IDecisionTrackerService
{
    private static readonly string[] KeywordTriggers =
    {
        "we should",
        "let's use",
        "lets use",
        "approved",
        "agreed",
    };



    private readonly AgentCallerTool _caller;
    private readonly ILogger<DecisionTrackerService> _logger;

    public DecisionTrackerService(AgentCallerTool caller, ILogger<DecisionTrackerService> logger)
    {
        _caller = caller;
        _logger = logger;
    }

    public Task<DecisionDetection?> DetectAsync(
        string userText,
        string assistantText,
        Guid? conversationId,
        CancellationToken ct = default)
    {
        // ✅ Guard against meta turns / prompt text (prevents loops)
        if (IsMeta(userText) || IsMeta(assistantText))
            return Task.FromResult<DecisionDetection?>(null);

        // Check BOTH user + assistant text for triggers (more accurate)
        var combined = $"{userText}\n{assistantText}";

        if (!ContainsTrigger(combined, out var trigger))
            return Task.FromResult<DecisionDetection?>(null);

        // Simple confidence heuristic:
        // approvals ("approved/agreed") are usually stronger than "we should"
        var conf =
            trigger is "approved" or "agreed" ? 0.90 :
            trigger.Contains("let") ? 0.80 :
            0.70;

        var excerpt = BuildExcerpt(combined, trigger);

        return Task.FromResult<DecisionDetection?>(
            new DecisionDetection(
                IsDecision: true,
                ConfidenceScore: conf,
                Trigger: trigger,
                Excerpt: excerpt,
                Mode: "keyword",
                ConversationId: conversationId));
    }

    public Task<DecisionDraft?> BuildDraftAsync(
        string userText,
        string assistantText,
        DecisionDetection detection,
        Guid? conversationId,
        string? ownerUserId,
        CancellationToken ct = default)
    {
        try
        {
            // Heuristic “decision sentence” extraction:
            // Prefer sentence containing the trigger, else first sentence of assistant.
            var combined = $"{userText}\n{assistantText}";
            var decisionSentence = ExtractSentenceAroundTrigger(combined, detection.Trigger);

            if (string.IsNullOrWhiteSpace(decisionSentence))
                decisionSentence = FirstSentence(assistantText);

            if (string.IsNullOrWhiteSpace(decisionSentence))
                return Task.FromResult<DecisionDraft?>(null);

            // Rationale: first paragraph (trim) or null
            var rationale = FirstParagraph(assistantText);
            if (!string.IsNullOrWhiteSpace(rationale) && rationale.Length > 400)
                rationale = rationale[..400].Trim();

            return Task.FromResult<DecisionDraft?>(
                new DecisionDraft(
                    Decision: NormalizeDecision(decisionSentence),
                    Rationale: string.IsNullOrWhiteSpace(rationale) ? null : rationale,
                    CreatedOnUtc: DateTime.UtcNow,
                    OwnerUserId: ownerUserId,
                    RelatedDocuments: new List<string>(), // you can enrich later from attachments
                    ConfidenceScore: detection.ConfidenceScore,
                    ConversationId: conversationId,
                    SourceMode: detection.Mode));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Decision draft build failed.");
            return Task.FromResult<DecisionDraft?>(null);
        }
    }

    // -----------------------------
    // Helpers
    // -----------------------------

    private static bool IsMeta(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant();

        if (t is "yes" or "y" or "no" or "n" or "ok" or "okay" or "cancel")
            return true;

        if (t.Contains("potential decision detected")) return true;
        if (t.Contains("record this as a formal decision")) return true;
        if (t.Contains("decision recorded")) return true;

        return false;
    }

    private static bool ContainsTrigger(string text, out string trigger)
    {
        trigger = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        var lower = text.ToLowerInvariant();
        foreach (var k in KeywordTriggers)
        {
            if (lower.Contains(k))
            {
                trigger = k;
                return true;
            }
        }
        return false;
    }

    private static string BuildExcerpt(string text, string trigger)
    {
        var clean = (text ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
        clean = Regex.Replace(clean, @"\s+", " ");

        var lower = clean.ToLowerInvariant();
        var idx = lower.IndexOf(trigger.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var start = Math.Max(0, idx - 60);
            var len = Math.Min(clean.Length - start, 200);
            return clean.Substring(start, len).Trim();
        }

        return clean.Length <= 200 ? clean : clean.Substring(0, 200).Trim();
    }

    private static string ExtractSentenceAroundTrigger(string text, string trigger)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(trigger))
            return "";

        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        var lower = normalized.ToLowerInvariant();

        var idx = lower.IndexOf(trigger.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";

        // naive sentence boundaries
        var start = normalized.LastIndexOf('.', Math.Max(0, idx));
        start = start < 0 ? 0 : start + 1;

        var end = normalized.IndexOf('.', idx);
        end = end < 0 ? normalized.Length : end + 1;

        var sentence = normalized.Substring(start, end - start).Trim();
        return sentence.Length > 0 ? sentence : "";
    }

    private static string FirstSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim();
        var idx = t.IndexOf('.');
        return idx > 0 ? t[..(idx + 1)].Trim() : t;
    }

    private static string FirstParagraph(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim();
        var split = t.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.None);
        return split.Length > 0 ? split[0].Trim() : t;
    }

    private static string NormalizeDecision(string s)
    {
        s = (s ?? "").Trim();
        s = Regex.Replace(s, @"\s+", " ");
        return s.TrimEnd('.', ';', ':') + ".";
    }
}

public sealed record DecisionDetection(
    bool IsDecision,
    double ConfidenceScore,
    string Trigger,
    string Excerpt,
    string Mode,
    Guid? ConversationId);

public sealed record DecisionDraft(
    string Decision,
    string? Rationale,
    DateTime CreatedOnUtc,
    string? OwnerUserId,
    List<string> RelatedDocuments,
    double ConfidenceScore,
    Guid? ConversationId,
    string SourceMode);