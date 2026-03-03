// File: Services/Email/Office365EmailService.cs
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
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
    private readonly GraphOptions _opt;
    private readonly TokenCredential _cred;

    public Office365EmailService(HttpClient http, IOptions<GraphOptions> opt)
    {
        _http = http;
        _opt = opt.Value;

        _cred = new ClientSecretCredential(_opt.TenantId, _opt.ClientId, _opt.ClientSecret);
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

        if (string.IsNullOrWhiteSpace(_opt.FromUser))
            throw new InvalidOperationException("Graph:FromUser is not configured.");

        var token = await _cred.GetTokenAsync(
            new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }),
            ct);

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(_opt.FromUser)}/sendMail");

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

        if (string.IsNullOrWhiteSpace(_opt.FromUser))
            throw new InvalidOperationException("Graph:FromUser is not configured.");

        var token = await _cred.GetTokenAsync(
            new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }),
            ct);

        // Create a draft message in FromUser mailbox (Drafts folder)
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(_opt.FromUser)}/messages");

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