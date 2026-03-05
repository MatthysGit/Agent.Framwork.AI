using System.ComponentModel;

namespace Ai.AgentFramwork.Massar.Web.Tools;

public sealed class ExcelAnalyticsToolWrapper
{
    private readonly ExcelAnalyticsTool _inner;

    public ExcelAnalyticsToolWrapper(ExcelAnalyticsTool inner) => _inner = inner;

    [Description("Analyze an uploaded Excel attachment. attachmentId MUST be a GUID string. Returns JSON with Text+Html+Charts.")]
    public Task<string> AnalyzeUploadedExcelAsync(
        [Description("Attachment Id as GUID string")] string attachmentId,
        [Description("User question")] string question)
    {
        if (!Guid.TryParse(attachmentId, out var id))
            return Task.FromResult(@"{""Text"":""Invalid attachment id."",""Html"":"""",""Charts"":[]}");

        return _inner.AnalyzeUploadedExcelAsync(id, question);
    }
}
