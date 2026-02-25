using System.Runtime.CompilerServices;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.EntityFrameworkCore;

namespace Ai.AgentFramwork.Massar.Web.Services.ChartQuestions;

public sealed class ChartQuestionTool : IChartQuestionTool
{
    // Tune this to balance speed vs. incremental UI updates.
    private const int BatchSize = 5;
    private readonly IChartQuestionAgent _agent;
    private readonly AppDbContext _db;

    public ChartQuestionTool(AppDbContext db, IChartQuestionAgent agent)
    {
        _db = db;
        _agent = agent;
    }

    public async Task<ChartQuestionResult> GenerateAsync(
        IReadOnlyList<string> chartTypes,
        int questionsPerChartType,
        IReadOnlyCollection<string> teamRoleKeys,
        IProgress<ChartQuestionProgress> progress,
        CancellationToken ct)
    {
        // Backwards-compatible: aggregate streamed questions into a single result.
        // ChartQuestionResult requires an IReadOnlyDictionary<string, IReadOnlyList<string>>.
        var bucket = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var total = CalculateExpectedTotal(chartTypes, questionsPerChartType, teamRoleKeys);
        var current = 0;

        progress.Report(new ChartQuestionProgress("Init", "Starting question generation...", current, total));

        await foreach (var item in GenerateStreamAsync(chartTypes, questionsPerChartType, teamRoleKeys, ct))
        {
            current = item.Current;
            progress.Report(new ChartQuestionProgress(item.Phase, item.Message, item.Current, item.Total));

            if (!string.IsNullOrWhiteSpace(item.Question))
            {
                if (!bucket.TryGetValue(item.ChartType, out var list))
                {
                    list = new List<string>();
                    bucket[item.ChartType] = list;
                }

                list.Add(item.Question);
            }
        }

        progress.Report(new ChartQuestionProgress("Done", "Question generation complete.", total, total));

        var ro = bucket.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value,
            StringComparer.OrdinalIgnoreCase);

        return new ChartQuestionResult(ro);
    }

    public async IAsyncEnumerable<ChartQuestionStreamItem> GenerateStreamAsync(
        IReadOnlyList<string> chartTypes,
        int questionsPerChartType,
        IReadOnlyCollection<string> teamRoleKeys,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (chartTypes is null || chartTypes.Count == 0)
            throw new ArgumentException("chartTypes cannot be empty.", nameof(chartTypes));
        if (questionsPerChartType <= 0)
            throw new ArgumentOutOfRangeException(nameof(questionsPerChartType));
        if (teamRoleKeys is null || teamRoleKeys.Count == 0)
            throw new ArgumentException("teamRoleKeys cannot be empty.", nameof(teamRoleKeys));

        // 1) Read schema catalog once
        var payload = await BuildSchemaPayloadAsync(ct);

        var total = CalculateExpectedTotal(chartTypes, questionsPerChartType, teamRoleKeys);
        var current = 0;

        // 2) Stream question-by-question (batched calls to the agent for speed).
        // We request a small batch per agent call, then yield each returned question individually.
        foreach (var chartType in chartTypes)
        foreach (var roleKey in teamRoleKeys)
        {
            var producedForPair = 0;

            while (producedForPair < questionsPerChartType)
            {
                ct.ThrowIfCancellationRequested();

                var remaining = questionsPerChartType - producedForPair;
                var take = Math.Min(BatchSize, remaining);

                var phase = "Agent";
                var msg =
                    $"Generating {chartType} questions ({producedForPair + 1}-{producedForPair + take}/{questionsPerChartType}) for {roleKey}...";

                ChartQuestionResult? agentResult = null;
                ChartQuestionStreamItem? toYield = null;

                try
                {
                    agentResult = await _agent.GenerateAsync(
                        payload,
                        new[] { chartType },
                        take,
                        new[] { roleKey },
                        ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Count this as one attempted "slot" so we don't stall forever.
                    producedForPair++;
                    current++;

                    toYield = new ChartQuestionStreamItem(
                        chartType,
                        roleKey,
                        null,
                        current,
                        total,
                        "Error",
                        $"Failed to generate question: {ex.Message}");
                }

                // yield outside catch
                if (toYield is not null)
                {
                    yield return toYield;
                    continue;
                }

                // Pull questions out of the agent result (may be fewer than requested).
                var returned = agentResult?.QuestionsByChartType is not null
                               && agentResult.QuestionsByChartType.TryGetValue(chartType, out var qs)
                               && qs is not null
                    ? qs.Where(q => !string.IsNullOrWhiteSpace(q)).ToList()
                    : new List<string>();

                if (returned.Count == 0)
                {
                    // No question returned: still advance one slot to avoid infinite loop.
                    producedForPair++;
                    current++;

                    yield return new ChartQuestionStreamItem(
                        chartType,
                        roleKey,
                        null,
                        current,
                        total,
                        phase,
                        $"No safe question produced for {chartType} ({roleKey}).");
                    continue;
                }

                foreach (var q in returned)
                {
                    if (producedForPair >= questionsPerChartType)
                        break;

                    producedForPair++;
                    current++;

                    yield return new ChartQuestionStreamItem(
                        chartType,
                        roleKey,
                        q,
                        current,
                        total,
                        phase,
                        $"Generated {chartType} question {producedForPair}/{questionsPerChartType} for {roleKey}."
                    );
                }
            }
        }
    }

    private static int CalculateExpectedTotal(
        IReadOnlyList<string> chartTypes,
        int questionsPerChartType,
        IReadOnlyCollection<string> teamRoleKeys)
    {
        var ctCount = chartTypes?.Count ?? 0;
        var roleCount = teamRoleKeys?.Count ?? 0;
        return Math.Max(1, ctCount * Math.Max(1, questionsPerChartType) * Math.Max(1, roleCount));
    }

    private async Task<object> BuildSchemaPayloadAsync(CancellationToken ct)
    {
        // 1) Read schema catalog
        var tables = await _db.TableDefinitions.AsNoTracking()
            .Select(t => new { t.Name, t.Definition })
            .ToListAsync(ct);

        var fields = await _db.TableFieldDefinitions.AsNoTracking()
            .Select(f => new
            {
                f.table_name,
                f.column_name,
                f.data_type,
                f.definition
            })
            .ToListAsync(ct);

        var refs = await _db.TableReferencings.AsNoTracking()
            .Select(r => new
            {
                r.table_name,
                r.referencing_table_name,
                r.referencing_column_name
            })
            .ToListAsync(ct);

        // 2) Build compact payload (token-friendly)
        var fieldsByTable = fields
            .GroupBy(f => f.table_name)
            .ToDictionary(
                g => g.Key,
                g => g.Select(f => new FieldInfoDto
                {
                    Name = f.column_name,
                    Type = f.data_type,
                    Definition = f.definition
                }).ToList(),
                StringComparer.OrdinalIgnoreCase);

        return new
        {
            tableCount = tables.Count,
            tables = tables
                .OrderBy(t => t.Name)
                .Select(t => new
                {
                    name = t.Name,
                    definition = t.Definition,
                    fields = fieldsByTable.TryGetValue(t.Name, out var fl)
                        ? fl
                        : new List<FieldInfoDto>()
                })
                .ToList(),
            referencing = refs
                .Select(r => new
                {
                    referencedTable = r.table_name,
                    referencingTable = r.referencing_table_name,
                    referencingColumn = r.referencing_column_name
                })
                .ToList()
        };
    }

    private sealed class FieldInfoDto
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? Definition { get; set; }
    }
}