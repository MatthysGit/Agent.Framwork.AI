namespace Ai.AgentFramwork.Massar.Web.Models;

public sealed record ExcelChartArtifact(
    string Title,
    string FileName,
    string ContentType,
    string Base64);

public sealed record ExcelAnalysisAgentResponse(
    string Text,
    string Html,
    IReadOnlyList<ExcelChartArtifact> Charts);
