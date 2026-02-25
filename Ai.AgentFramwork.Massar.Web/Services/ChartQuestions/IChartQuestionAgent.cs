namespace Ai.AgentFramwork.Massar.Web.Services.ChartQuestions;

public interface IChartQuestionAgent
{
    Task<ChartQuestionResult> GenerateAsync(
        object schemaPayload,
        IReadOnlyList<string> chartTypes,
        int questionsPerChartType,
        IReadOnlyCollection<string> teamRoleKeys,
        CancellationToken ct);
}
