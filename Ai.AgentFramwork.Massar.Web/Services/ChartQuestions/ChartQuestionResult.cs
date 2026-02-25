namespace Ai.AgentFramwork.Massar.Web.Services.ChartQuestions;

public sealed record ChartQuestionResult(
    IReadOnlyDictionary<string, IReadOnlyList<string>> QuestionsByChartType);
