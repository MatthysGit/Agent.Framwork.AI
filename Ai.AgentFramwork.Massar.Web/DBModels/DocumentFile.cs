using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class DocumentFile
{
    public Guid DocumentFileId { get; set; }

    public Guid DocumentId { get; set; }

    public string FileName { get; set; } = null!;

    public string ContentType { get; set; } = null!;

    public string? FileExtension { get; set; }

    public long FileSizeBytes { get; set; }

    public byte[] FileContent { get; set; } = null!;

    public DateTime CreatedUtc { get; set; }

    public virtual Document Document { get; set; } = null!;
}
