using System;

namespace Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;

public enum DocStore
{
    Global = 1,
    Scoped = 2
}

public sealed record DocumentSearchHit(
    DocStore Store,
    double Score,
    string Title,
    string Snippet,
    int? PageNumber,
    Guid? DocumentId,
    Guid? DocumentFileId,
    Guid? AttachmentId
);

public sealed record DocumentAttachmentDescriptor(
    DocStore Store,
    Guid Id,            // Global => DocumentFileId, Scoped => AttachmentId
    string FileName
);

public sealed record DocumentSearchAgentResponse(
    string Text,
    DocumentAttachmentDescriptor[] Attachments
);