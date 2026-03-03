namespace Ai.AgentFramwork.Massar.Web.Services.Email;

public sealed class GraphOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    // Mailbox to send from (UPN/email), e.g. noreply@yourdomain.com
    public string FromUser { get; set; } = "";
}