// File: Services/Email/Office365EmailService.cs
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using System.Linq;

namespace Ai.AgentFramwork.Massar.Web.Services.Email;

public sealed record DraftResult(string Id, string? WebLink);

public interface IOffice365EmailService
{
    Task SendAsync(
        string[] to,
        string subject,
        string htmlBody,
        string[]? cc = null,
        bool saveToSentItems = true,
        CancellationToken ct = default);

    Task<DraftResult?> CreateDraftAsync(
        string[] to,
        string subject,
        string htmlBody,
        string[]? cc = null,
        CancellationToken ct = default);
}

public sealed class Office365EmailService : IOffice365EmailService
{
    private readonly HttpClient _http;
    private readonly IGraphOptionsProvider _graphOpts;

    public Office365EmailService(HttpClient http, IGraphOptionsProvider graphOpts)
    {
        _http = http;
        _graphOpts = graphOpts;
    }

    private static TokenCredential CreateCredential(GraphOptions opt)
        => new ClientSecretCredential(opt.TenantId, opt.ClientId, opt.ClientSecret);

    private async Task<(GraphOptions Opt, AccessToken Token)> GetTokenAsync(CancellationToken ct)
    {
        var opt = await _graphOpts.GetAsync(ct);

        if (string.IsNullOrWhiteSpace(opt.TenantId))
            throw new InvalidOperationException("Graph TenantId is not configured in the database.");

        if (string.IsNullOrWhiteSpace(opt.ClientId))
            throw new InvalidOperationException("Graph ClientId is not configured in the database.");

        if (string.IsNullOrWhiteSpace(opt.ClientSecret))
            throw new InvalidOperationException("Graph ClientSecret is not configured in the database.");

        if (string.IsNullOrWhiteSpace(opt.FromUser))
            throw new InvalidOperationException("Graph FromUser is not configured in the database.");

        var cred = CreateCredential(opt);

        var token = await cred.GetTokenAsync(
            new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }),
            ct);

        return (opt, token);
    }

    public async Task SendAsync(
        string[] to,
        string subject,
        string htmlBody,
        string[]? cc = null,
        bool saveToSentItems = true,
        CancellationToken ct = default)
    {
        if (to is null || to.Length == 0)
            throw new ArgumentException("At least one recipient is required.", nameof(to));

        var (opt, token) = await GetTokenAsync(ct);

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(opt.FromUser)}/sendMail");

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        static object Recipients(string[] emails) =>
            emails.Select(e => new { emailAddress = new { address = e } });

        var payload = new
        {
            message = new
            {
                subject = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject,
                body = new { contentType = "HTML", content = htmlBody },
                toRecipients = Recipients(to),
                ccRecipients = (cc is { Length: > 0 }) ? Recipients(cc) : Array.Empty<object>()
            },
            saveToSentItems
        };

        req.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Graph sendMail failed: {(int)res.StatusCode} {res.ReasonPhrase}\n{body}");
        }
    }

    public async Task<DraftResult?> CreateDraftAsync(
        string[] to,
        string subject,
        string htmlBody,
        string[]? cc = null,
        CancellationToken ct = default)
    {
        if (to is null || to.Length == 0)
            throw new ArgumentException("At least one recipient is required.", nameof(to));

        var (opt, token) = await GetTokenAsync(ct);

        // Create a draft message in FromUser mailbox (Drafts folder)
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(opt.FromUser)}/messages");

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        static object Recipients(string[] emails) =>
            emails.Select(e => new { emailAddress = new { address = e } });

        var payload = new
        {
            subject = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject,
            body = new { contentType = "HTML", content = htmlBody },
            toRecipients = Recipients(to),
            ccRecipients = (cc is { Length: > 0 }) ? Recipients(cc) : Array.Empty<object>()
        };

        req.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var res = await _http.SendAsync(req, ct);
        var resBody = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Graph create draft failed: {(int)res.StatusCode} {res.ReasonPhrase}\n{resBody}");
        }

        // Graph returns the created message, including id and (often) webLink
        try
        {
            var msg = JsonSerializer.Deserialize<GraphMessageResponse>(resBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (msg?.Id is null)
                return null;

            return new DraftResult(msg.Id, msg.WebLink);
        }
        catch
        {
            // Draft created successfully, but parsing failed — still return "saved" with no link
            return new DraftResult(Id: "(created)", WebLink: null);
        }
    }

    private sealed class GraphMessageResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("webLink")]
        public string? WebLink { get; set; }
    }
}