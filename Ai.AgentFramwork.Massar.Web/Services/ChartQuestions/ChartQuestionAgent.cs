using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Services.ChartQuestions;

public sealed class ChartQuestionAgent : IChartQuestionAgent
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly AIAgent _agent;

    public ChartQuestionAgent(IConfiguration configuration)
    {
        var apiKey =
            configuration["OpenAI:ApiKey"]
            ?? configuration["OpenAI:Key"]
            ?? configuration["OPENAI_API_KEY"]
            ?? throw new InvalidOperationException(
                "OpenAI API key not configured. Set OpenAI:ApiKey (or OpenAI:Key) / OPENAI_API_KEY.");

        var model =
            configuration["OpenAI:ChatModel"]
            ?? configuration["OpenAI:Model"]
            ?? configuration["OPENAI_CHAT_MODEL_ID"]
            ?? "gpt-4o-mini";

        var client = new OpenAIClient(apiKey);
        var chatClient = client.GetChatClient(model);

        _agent = chatClient.AsAIAgent(
            name: "ChartQuestionAgent",
            instructions: """
                          You generate chat-style questions that drive database queries and chart rendering, tailored to multiple Team + Role audiences.

                          Input: JSON schema payload describing tables, fields, datatypes, and foreign key references.
                          Output: STRICT JSON only (no markdown) matching:

                          {
                            "questionsByChartType": {
                              "Bar": ["...", "..."],
                              "Column": ["..."],
                              ...
                            }
                          }

                          Rules:
                          - Each question MUST be phrased as if the intended ROLE is chatting with the system.
                            - Start conversationally (examples):
                              - "As a <Role> in <Team>, can you..."
                              - "I'm a <Role> — please..."
                            - The body MUST still include these lines (same meaning, wording can vary slightly):
                              - "From the database, <AGG>(<Measure>) grouped by <DimensionField>."
                              - "Then generate a <CHARTTYPE> chart image titled \"<Title>\"."
                              - "X-axis: <DimensionField>"
                              - "Y-axis: <AggLabel>"

                          - Use only fields that exist in the provided schema payload.
                          - Questions should be relevant to EACH provided audience (Team + Role) (e.g., Sales Managers focus on pipeline/revenue; HR Payroll focuses on payroll/headcount; Manufacturing Inventory focuses on stock/throughput). If the schema doesn't support a perfect match, choose the closest safe business metric.
                          - Prefer safe aggregations:
                            - COUNT(*) grouped by categorical fields
                            - SUM/AVG only if numeric-like fields are present (int/decimal/money/float)
                            - For Line/Area: group by a date field if present (by Month/Year).
                            - For Donut: use small category groups.
                            - For Gauge/Progress: use a single KPI (SUM/COUNT) compared to a target or previous period if date exists.
                          - Avoid huge cardinality dimensions (IDs, GUIDs). Prefer Name/Type/Status/Category.
                          - For EACH chart type, aim for N questions PER audience.
                          - Return up to (N * number_of_audiences) questions per chart type total.
                          - If you cannot produce N safely for an audience, return fewer for that audience.
                          """);
    }

    public async Task<ChartQuestionResult> GenerateAsync(
        object schemaPayload,
        IReadOnlyList<string> chartTypes,
        int questionsPerChartType,
        IReadOnlyCollection<string> teamRoleKeys,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // teamRoleKeys are encoded as: "<Team>|<Role>" (pipe-delimited) to keep role names unique across teams.
        var audiences = teamRoleKeys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Split('|', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) &&
                            !string.IsNullOrWhiteSpace(parts[1]))
            .Select(parts => new { team = parts[0], role = parts[1] })
            .ToList();

        var input = new
        {
            requestedChartTypes = chartTypes,
            questionsPerChartType,
            audiences,
            schema = schemaPayload
        };

        var prompt = "Generate questions now. Return JSON ONLY.\n" + JsonSerializer.Serialize(input, JsonOpts);

        var response = await _agent.RunAsync(prompt);
        ct.ThrowIfCancellationRequested();

        var text = response.Messages
                       .LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text
                   ?? string.Empty;

        text = text.Trim();

        if (string.IsNullOrWhiteSpace(text))
            return new ChartQuestionResult(
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (!root.TryGetProperty("questionsByChartType", out var qbct) || qbct.ValueKind != JsonValueKind.Object)
            return new ChartQuestionResult(
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

        var dict = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var chartProp in qbct.EnumerateObject())
        {
            if (chartProp.Value.ValueKind != JsonValueKind.Array) continue;

            var list = new List<string>();
            foreach (var item in chartProp.Value.EnumerateArray())
            {
                var q = item.GetString();
                if (!string.IsNullOrWhiteSpace(q))
                    list.Add(q.Trim());
            }

            dict[chartProp.Name] = list;
        }

        return new ChartQuestionResult(dict);
    }
}