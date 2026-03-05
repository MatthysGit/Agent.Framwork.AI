// File: Services/Chat/PromptSanitizer.cs
using System;
using System.Text.RegularExpressions;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

/// <summary>
/// Reduces token usage by removing hidden TOOL payload markers and trimming long text.
/// Safe to use before sending chat history into an LLM prompt.
/// </summary>
public static class PromptSanitizer
{
    // Removes both legacy <!--TOOL:...--> and markdown comment [//]: # (TOOL:...).
    private static readonly Regex ToolMarkerRegex =
        new(@"(?:<!--\s*TOOL:.*?-->)|(?:\[\s*\/\/\s*\]\s*:\s*#\s*\(\s*TOOL:.*?\)\s*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static string StripToolPayloads(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return ToolMarkerRegex.Replace(text, string.Empty).Trim();
    }

    public static string TrimTo(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (maxChars < 32) maxChars = 32;
        return text.Length <= maxChars ? text : text.Substring(0, maxChars) + "…";
    }
}