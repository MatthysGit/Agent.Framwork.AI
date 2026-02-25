namespace Ai.AgentFramwork.Massar.Web.Services.ChartQuestions;

public sealed record ChartQuestionProgress(
    string Phase,
    string Message,
    int? Current = null,
    int? Total = null);
