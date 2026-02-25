// SchemaDefinitionAgent.cs
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using System.Text.Json;

namespace Ai.AgentFramwork.Massar.Web.Services.SchemaIntrospection;

public sealed class SchemaDefinitionAgent : ISchemaDefinitionAgent
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly AIAgent _agent;

    public SchemaDefinitionAgent(IConfiguration configuration)
    {
        // Match your ChatAgentFactory approach: OpenAIClient -> chat client -> AsAIAgent(...)
        // Try common config keys to avoid breaking existing appsettings.
        var apiKey =
            configuration["OpenAI:ApiKey"]
            ?? configuration["OpenAI:Key"]
            ?? configuration["OPENAI_API_KEY"]
            ?? throw new InvalidOperationException("OpenAI API key not configured. Set OpenAI:ApiKey or OPENAI_API_KEY.");

        var model =
            configuration["OpenAI:ChatModel"]
            ?? configuration["OpenAI:Model"]
            ?? configuration["OPENAI_CHAT_MODEL_ID"]
            ?? "gpt-4o-mini"; // safe default; override in config

        var client = new OpenAIClient(apiKey);
        var chatClient = client.GetChatClient(model);

        // Create an Agent Framework agent (same unified AIAgent interface).
        _agent = chatClient.AsAIAgent(
            name: "SchemaDefinitionAgent",
            instructions: """
You are a data catalog assistant for SQL Server.
Return STRICT JSON only (no markdown) matching this schema:

{
  "tableDefinition": "string (1-3 sentences)",
  "columns": {
     "ColumnName": "string (short definition)"
  }
}

Rules:
- Do not invent business meaning that isn't supported by column names/samples.
- If uncertain, be explicit ("Likely ...", "Appears to be ...").
- Column definitions must be short and practical.
""");
    }

    public async Task<(string TableDefinition, Dictionary<string, string> ColumnDefinitions)> GenerateDefinitionsAsync(
        string tableName,
        IReadOnlyList<(string Column, string SqlType)> columns,
        IReadOnlyDictionary<string, IReadOnlyList<string>> samples,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Keep the payload compact; don’t send huge sample arrays.
        var payload = new
        {
            table = tableName,
            columns = columns.Select(c => new { name = c.Column, type = c.SqlType }).ToArray(),
            samples = samples.ToDictionary(k => k.Key, v => v.Value.Take(12).ToArray())
        };

        var prompt =
            "Analyze this table metadata & sample values and return JSON ONLY:\n"
            + JsonSerializer.Serialize(payload, JsonOpts);

        // Agent Framework call (non-streaming)
        // Many versions expose RunAsync(string). Some also support cancellation token overloads.
        // If your package version supports ct overload, feel free to switch to that.
        var response = await _agent.RunAsync(prompt);

        ct.ThrowIfCancellationRequested();

        var text = response.Messages
           .LastOrDefault(m => m.Role == ChatRole.Assistant)?
           .Text
           ?? string.Empty;

        text = text.Trim();

        if (string.IsNullOrWhiteSpace(text))
            return ("", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        // Parse strict JSON output
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var tableDef = root.TryGetProperty("tableDefinition", out var td)
                ? td.GetString() ?? ""
                : "";

            var colDefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("columns", out var colsEl) && colsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in colsEl.EnumerateObject())
                {
                    var def = p.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(def))
                        colDefs[p.Name] = def!;
                }
            }

            return (tableDef, colDefs);
        }
        catch (JsonException)
        {
            // If the model ever returns non-JSON, fail safely without poisoning your catalog.
            return ("", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }
}
