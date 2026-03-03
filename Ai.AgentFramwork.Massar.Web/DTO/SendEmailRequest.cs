namespace Ai.AgentFramwork.Massar.Web.DTO;

public sealed record SendEmailRequest(
    string[] To,
    string Subject,
    string BodyMarkdown,
    string[]? Cc,
    bool SaveToSentItems = true);