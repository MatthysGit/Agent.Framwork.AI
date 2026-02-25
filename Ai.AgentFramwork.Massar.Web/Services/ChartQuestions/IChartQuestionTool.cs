namespace Ai.AgentFramwork.Massar.Web.Services.ChartQuestions;

public interface IChartQuestionTool
{
    Task<ChartQuestionResult> GenerateAsync(
        IReadOnlyList<string> chartTypes,
        int questionsPerChartType,
        IReadOnlyCollection<string> teamRoleKeys,
        IProgress<ChartQuestionProgress> progress,
        CancellationToken ct);

    /// <summary>
    /// Streams generated questions as they are produced.
    /// </summary>
    IAsyncEnumerable<ChartQuestionStreamItem> GenerateStreamAsync(
        IReadOnlyList<string> chartTypes,
        int questionsPerChartType,
        IReadOnlyCollection<string> teamRoleKeys,
        CancellationToken ct);
}

/// <summary>
/// A single streamed question item.
/// </summary>
public sealed record ChartQuestionStreamItem(
    string ChartType,
    string TeamRoleKey,
    string? Question,
    int Current,
    int Total,
    string Phase,
    string Message);
