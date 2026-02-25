namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public sealed record RetrievalHit(
    string Source,          // "authorized" | "chat"
    string Text,
    double Score,
    Guid? DocumentId = null,
    Guid? AttachmentId = null,
    Guid? ConversationId = null
);