using Ai.AgentFramwork.Massar.Web.Models;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Services.Documents;

namespace Ai.AgentFramwork.Massar.Web.Services.ExcelServices;

public sealed class UploadedExcelReader : IUploadedExcelReader
{
    private readonly IChatAttachmentBlobReader _blob;
    private readonly ISpreadsheetTableExtractor _tables;

    public UploadedExcelReader(IChatAttachmentBlobReader blob, ISpreadsheetTableExtractor tables)
    {
        _blob = blob;
        _tables = tables;
    }

    public async Task<TabularData> ReadAsTableAsync(Guid attachmentId, CancellationToken ct = default)
    {
        var (bytes, fileName, contentType) = await _blob.ReadAsync(attachmentId, ct);
        return await _tables.ExtractTableAsync(bytes, contentType, fileName, sheetName: null, ct: ct);
    }
}
