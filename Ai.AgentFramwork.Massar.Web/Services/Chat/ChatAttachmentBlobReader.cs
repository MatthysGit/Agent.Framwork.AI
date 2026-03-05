using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

public sealed class ChatAttachmentBlobReader : IChatAttachmentBlobReader
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ChatAttachmentBlobReader(IDbContextFactory<AppDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task<(byte[] Bytes, string FileName, string ContentType)> ReadAsync(Guid attachmentId, CancellationToken ct = default)
    {
        if (attachmentId == Guid.Empty)
            throw new ArgumentException("AttachmentId is required.", nameof(attachmentId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP (1) FileContent, FileName, ContentType
FROM dbo.ChatAttachmentBlob
WHERE AttachmentId = @id;";

        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = attachmentId;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Attachment not found in dbo.ChatAttachmentBlob.");

        var bytes = (byte[])reader["FileContent"];
        var fileName = reader["FileName"]?.ToString() ?? "attachment.bin";
        var contentType = reader["ContentType"]?.ToString() ?? "application/octet-stream";

        return (bytes, fileName, contentType);
    }
}