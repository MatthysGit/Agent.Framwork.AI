using System.Net.Http.Headers;
using System.Security;
using System.Text.Json;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

public sealed class BraveSearchClient
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _http;

    public BraveSearchClient(IConfiguration configuration, HttpClient http)
    {
        _configuration = configuration;
        _http = http;
    }

    public async Task<IEnumerable<string>> SearchAsync(string query, int count = 5, string country = "US", string searchLang = "en")
    {
        count = Math.Clamp(count, 1, 10);

        var key = _configuration["Brave:Key"] ?? _configuration["BraveSearch:Key"];
        if (string.IsNullOrWhiteSpace(key))
            return new[] { "<error>Brave Search API key is not configured. Set Brave:Key in user-secrets/appsettings.</error>" };

        var endpoint = _configuration["Brave:Endpoint"]
            ?? _configuration["BraveSearch:Endpoint"]
            ?? "https://api.search.brave.com/res/v1/web/search";

        var url =
            $"{endpoint}?q={Uri.EscapeDataString(query)}&count={count}&country={Uri.EscapeDataString(country)}&search_lang={Uri.EscapeDataString(searchLang)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        req.Headers.TryAddWithoutValidation("X-Subscription-Token", key);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            return new[] { $"<error>Web search failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. {Truncate(body, 400)}</error>" };
        }

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        var results = new List<string>();

        if (doc.RootElement.TryGetProperty("web", out var web) &&
            web.TryGetProperty("results", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in arr.EnumerateArray())
            {
                if (results.Count >= count) break;

                var title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
                var link = r.TryGetProperty("url", out var u) ? u.GetString() : null;
                var desc = r.TryGetProperty("description", out var d) ? d.GetString() : null;

                if (string.IsNullOrWhiteSpace(link))
                    continue;

                title ??= link;
                desc ??= string.Empty;

                results.Add($"<result title=\"{XmlEscape(title)}\" url=\"{XmlEscape(link)}\">{XmlEscape(Truncate(desc, 220))}</result>");
            }
        }

        return results.Count == 0
            ? new[] { "<result>No results.</result>" }
            : results;
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    private static string XmlEscape(string? s)
        => string.IsNullOrEmpty(s) ? "" : (SecurityElement.Escape(s) ?? "");
}
