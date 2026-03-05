using Ai.AgentFramwork.Massar.Web.Models;

namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public interface ISpreadsheetTableExtractor
{
    Task<TabularData> ExtractTableAsync(
        byte[] content,
        string contentType,
        string fileName,
        string? sheetName = null,
        CancellationToken ct = default);
}
