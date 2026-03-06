using System.Text.RegularExpressions;

namespace Ai.AgentFramwork.Massar.Web.Services.WhatIfs.Modelss;

public sealed record WhatIfRequest(
    string OriginalUserText,
    string MetricName,
    string VariableName,
    double ChangePercent,
    string ChangeDirection,
    string ScenarioName,
    string? GroupBy,
    bool IsTimeSeriesPreferred,
    int PreferredHistoryPoints,
    bool IsDecrease)
{
    public double Multiplier => 1d + (ChangePercent / 100d);

    public static WhatIfRequest Parse(string userText)
    {
        var text = (userText ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();

        var variable = DetectVariable(lower);
        var metric = DetectMetric(lower, variable);
        var groupBy = DetectGroupBy(text);
        var change = DetectChangePercent(lower, out var direction);
        var isDecrease = change < 0;
        var changePctAbs = Math.Abs(change);

        if (changePctAbs < 0.0001d)
        {
            direction = "increase";
            changePctAbs = 10d;
            change = 10d;
            isDecrease = false;
        }

        var signedLabel = (change >= 0 ? "+" : "-") + changePctAbs.ToString("0.##") + "%";
        var scenarioName = $"{ToTitle(variable)} {signedLabel}";

        var historyPoints = DetectHistoryWindow(lower);

        return new WhatIfRequest(
            OriginalUserText: text,
            MetricName: metric,
            VariableName: variable,
            ChangePercent: change,
            ChangeDirection: direction,
            ScenarioName: scenarioName,
            GroupBy: groupBy,
            IsTimeSeriesPreferred: !HasExplicitGroupingOnly(lower),
            PreferredHistoryPoints: historyPoints,
            IsDecrease: isDecrease
        );
    }

    private static string DetectVariable(string lower)
    {
        if (lower.Contains("price")) return "price";
        if (lower.Contains("discount")) return "discount";
        if (lower.Contains("cost")) return "cost";
        if (lower.Contains("expense")) return "cost";
        if (lower.Contains("margin")) return "margin";
        if (lower.Contains("profit")) return "profit";
        if (lower.Contains("revenue")) return "revenue";
        if (lower.Contains("sales")) return "sales";
        if (lower.Contains("volume")) return "volume";
        if (lower.Contains("demand")) return "demand";
        if (lower.Contains("headcount")) return "headcount";
        if (lower.Contains("employee")) return "headcount";
        if (lower.Contains("staff")) return "headcount";
        if (lower.Contains("marketing")) return "marketing spend";
        if (lower.Contains("spend")) return "spend";
        return "value";
    }

    private static string DetectMetric(string lower, string variable)
    {
        if (lower.Contains("revenue")) return "Revenue";
        if (lower.Contains("sales")) return "Sales";
        if (lower.Contains("profit")) return "Profit";
        if (lower.Contains("margin")) return "Margin";
        if (lower.Contains("cost") || lower.Contains("expense")) return "Cost";
        if (lower.Contains("order")) return "Orders";
        if (lower.Contains("headcount") || lower.Contains("employee") || lower.Contains("staff")) return "Headcount";

        return variable switch
        {
            "price" => "Revenue",
            "discount" => "Revenue",
            "marketing spend" => "Revenue",
            "volume" => "Sales",
            "demand" => "Sales",
            "headcount" => "Headcount",
            "cost" => "Cost",
            _ => "Value"
        };
    }

    private static string? DetectGroupBy(string text)
    {
        var m = Regex.Match(text ?? string.Empty, @"\bby\s+(?<g>[A-Za-z][A-Za-z\s]{1,40})", RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        var raw = m.Groups["g"].Value.Trim();
        var stopWords = new[]
        {
            "for", "in", "over", "during", "next", "last", "with", "if", "when", "where", "while", "assuming"
        };

        foreach (var stop in stopWords)
        {
            var idx = raw.IndexOf(" " + stop + " ", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                raw = raw[..idx].Trim();
                break;
            }
        }

        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static double DetectChangePercent(string lower, out string direction)
    {
        direction = "increase";

        var signedPct = Regex.Match(lower, @"(?<sign>[+-])\s*(?<num>\d+(?:\.\d+)?)\s*%");
        if (signedPct.Success && double.TryParse(signedPct.Groups["num"].Value, out var signedVal))
        {
            direction = signedPct.Groups["sign"].Value == "-" ? "decrease" : "increase";
            return direction == "decrease" ? -signedVal : signedVal;
        }

        var byPct = Regex.Match(lower, @"\b(increase|decrease|reduce|drop|lower|raise|grow|boost)\b[\w\s]{0,40}?\bby\b\s*(?<num>\d+(?:\.\d+)?)\s*%", RegexOptions.IgnoreCase);
        if (byPct.Success && double.TryParse(byPct.Groups["num"].Value, out var byVal))
        {
            var verb = byPct.Groups[1].Value.ToLowerInvariant();
            direction = verb is "decrease" or "reduce" or "drop" or "lower" ? "decrease" : "increase";
            return direction == "decrease" ? -byVal : byVal;
        }

        var nakedPct = Regex.Match(lower, @"(?<num>\d+(?:\.\d+)?)\s*%");
        if (nakedPct.Success && double.TryParse(nakedPct.Groups["num"].Value, out var nakedVal))
        {
            if (lower.Contains("decrease") || lower.Contains("reduce") || lower.Contains("drop") || lower.Contains("lower"))
            {
                direction = "decrease";
                return -nakedVal;
            }

            direction = "increase";
            return nakedVal;
        }

        if (lower.Contains("decrease") || lower.Contains("reduce") || lower.Contains("drop") || lower.Contains("lower"))
        {
            direction = "decrease";
            return -10d;
        }

        return 10d;
    }

    private static int DetectHistoryWindow(string lower)
    {
        var m = Regex.Match(lower, @"\b(last|next)\s+(?<n>\d{1,2})\s+(month|months|week|weeks|quarter|quarters|year|years)\b", RegexOptions.IgnoreCase);
        if (!m.Success) return 18;

        if (!int.TryParse(m.Groups["n"].Value, out var n))
            return 18;

        var unit = m.Groups[3].Value.ToLowerInvariant();
        return unit switch
        {
            "week" or "weeks" => Math.Clamp(n, 8, 52),
            "month" or "months" => Math.Clamp(n, 6, 36),
            "quarter" or "quarters" => Math.Clamp(n * 3, 6, 36),
            "year" or "years" => Math.Clamp(n * 12, 12, 60),
            _ => 18
        };
    }

    private static bool HasExplicitGroupingOnly(string lower)
        => lower.Contains("by region")
           || lower.Contains("by territory")
           || lower.Contains("by department")
           || lower.Contains("by category")
           || lower.Contains("by product")
           || lower.Contains("by customer")
           || lower.Contains("by segment")
           || lower.Contains("by channel");

    private static string ToTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Value";
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i].ToLowerInvariant();
            parts[i] = p.Length == 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..];
        }

        return string.Join(' ', parts);
    }
}
