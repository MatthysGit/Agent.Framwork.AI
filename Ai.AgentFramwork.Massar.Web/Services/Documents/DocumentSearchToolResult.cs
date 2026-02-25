namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public sealed class DocumentSearchToolResult
{
    public required string Query { get; init; }
    public required int TopK { get; init; }
    public required List<DocumentSearchToolHit> Hits { get; init; } = new();
    public required List<DocumentSearchToolFile> Files { get; init; } = new(); // only if CanDownload
}

public sealed class DocumentSearchToolHit
{
    public required Guid DocumentId { get; init; }
    public required string SourceName { get; init; }
    public required string ChunkText { get; init; }
    public required double Score { get; init; }
}

public sealed class DocumentSearchToolFile
{
    public required Guid DocumentId { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required string Base64Content { get; init; } // keep tool output JSON-safe
}