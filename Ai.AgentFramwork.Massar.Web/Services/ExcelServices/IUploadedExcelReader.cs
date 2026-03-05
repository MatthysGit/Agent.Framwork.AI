using Ai.AgentFramwork.Massar.Web.Models;

namespace Ai.AgentFramwork.Massar.Web.Services.ExcelServices;

public interface IUploadedExcelReader
{
    Task<TabularData> ReadAsTableAsync(Guid attachmentId, CancellationToken ct = default);
}
