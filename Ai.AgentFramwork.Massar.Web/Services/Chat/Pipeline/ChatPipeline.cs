using System.Text.Json;
using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Tools;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;

public sealed class ChatPipeline
{
    private readonly RouterAgent _router;
    private readonly AgentCallerTool _caller;
    private readonly ChatTools _chartTools;
    private readonly DocumentSearchTool _docSearchTool;
    private readonly DocumentEditTool _docEditTool;
    private readonly Func<Guid> _getConversationId;

    public ChatPipeline(
        RouterAgent router,
        AgentCallerTool caller,
        ChatTools chartTools,
        DocumentSearchTool docSearchTool,
        DocumentEditTool docEditTool,
        Func<Guid> getConversationId)
    {
        _router = router;
        _caller = caller;
        _chartTools = chartTools;
        _docSearchTool = docSearchTool;
        _docEditTool = docEditTool;
        _getConversationId = getConversationId;
    }

    public sealed record PipelineResult(string Text, string RoutedAgent, string RouterReason);

    public async Task<PipelineResult> ExecuteAsync(string userText, CancellationToken ct = default)
    {
        var r = await _router.RouteAsync(userText, ct);

        // Document search/edit are executed via tools so your attachment JSON stays identical.
        if (r.Agent.Equals(ChatAgentFactory.DocumentSearchAgentName, StringComparison.OrdinalIgnoreCase))
        {
            var convoId = _getConversationId();
            var json = await _docSearchTool.SearchDocumentsAsync(convoId, userText);
            return new PipelineResult(json ?? "No relevant information found.", r.Agent, r.Reason);
        }

        if (r.Agent.Equals(ChatAgentFactory.DocumentEditAgentName, StringComparison.OrdinalIgnoreCase))
        {
            var convoId = _getConversationId();
            var instruction = InferEditInstruction(userText);
            var json = await _docEditTool.EditDocumentAsync(convoId, userText, instruction);
            return new PipelineResult(json ?? "No edit result returned.", r.Agent, r.Reason);
        }

        // Chart path: SQL -> parse JSON -> chart tool -> markdown image only
        if (r.Mode.Equals("chart", StringComparison.OrdinalIgnoreCase))
        {
            var sql = await _caller.CallAgentAsync(ChatAgentFactory.SqlAgentName, userText, cancellationToken: ct);
            var chart = ParseChartJson(sql.Text);
            var url = await CreateChartAsync(chart);
            return new PipelineResult($"![chart]({url})", ChatAgentFactory.SqlAgentName, r.Reason);
        }

        // Default: call chosen agent
        var call = await _caller.CallAgentAsync(r.Agent, userText, cancellationToken: ct);
        return new PipelineResult(call.Text, call.AgentName, r.Reason);
    }

    private static string InferEditInstruction(string userText)
    {
        var lower = (userText ?? "").ToLowerInvariant();
        if (lower.Contains("comment")) return "add comments";
        if (lower.Contains("annotate")) return "annotate with comments";
        if (lower.Contains("highlight")) return "highlight issues and add comments";
        if (lower.Contains("proofread")) return "proofread and add comments";
        if (lower.Contains("revise") || lower.Contains("edit")) return "make suggested edits and add comments";
        return "add comments";
    }

    // Chart JSON schema support (same as orchestrator contract)
    private static ChartPayload ParseChartJson(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<ChartPayload>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (payload is null || string.IsNullOrWhiteSpace(payload.ChartType))
                throw new InvalidOperationException("Missing chartType.");

            return payload;
        }
        catch
        {
            return new ChartPayload { ChartType = "bar", Title = "Chart", Labels = ["No data"], Values = [0] };
        }
    }

    private Task<string> CreateChartAsync(ChartPayload p)
    {
        var t = p.ChartType?.Trim().ToLowerInvariant();

        return t switch
        {
            "bar" => _chartTools.CreateBarChartPngAsync(p.Title ?? "Chart", p.XAxisLabel ?? "", p.YAxisLabel ?? "", p.Labels ?? [], p.Values ?? []),
            "pie" => _chartTools.CreatePieChartPngAsync(p.Title ?? "Chart", p.Labels ?? [], p.Values ?? []),
            "line" => _chartTools.CreateLineChartPngAsync(p.Title ?? "Chart", p.XAxisLabel ?? "", p.YAxisLabel ?? "", p.Labels ?? [], p.Values ?? []),
            "area" => _chartTools.CreateAreaChartPngAsync(p.Title ?? "Chart", p.XAxisLabel ?? "", p.YAxisLabel ?? "", p.Labels ?? [], p.Values ?? []),
            "donut" => _chartTools.CreateDonutChartPngAsync(p.Title ?? "Chart", p.Labels ?? [], p.Values ?? []),
            "gauge" => _chartTools.CreateGaugeChartPngAsync(p.Title ?? "Gauge", p.Value ?? 0, p.Min ?? 0, p.Max ?? 100),
            "progress" => _chartTools.CreateProgressBarsChartPngAsync(p.Title ?? "Progress", p.Labels ?? [], p.Values ?? []),
            "multicolumn" => _chartTools.CreateMultiSeriesColumnChartPngAsync(
                p.Title ?? "Chart",
                p.XAxisLabel ?? "",
                p.YAxisLabel ?? "",
                p.Labels ?? [],
                p.Series ?? []),
            _ => _chartTools.CreateBarChartPngAsync(p.Title ?? "Chart", p.XAxisLabel ?? "", p.YAxisLabel ?? "", p.Labels ?? [], p.Values ?? []),
        };
    }

    public sealed class ChartPayload
    {
        public string? ChartType { get; set; }
        public string? Title { get; set; }
        public string? XAxisLabel { get; set; }
        public string? YAxisLabel { get; set; }
        public List<string>? Labels { get; set; }
        public List<double>? Values { get; set; }

        public double? Value { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }

        public List<ChartSeries>? Series { get; set; }
    }

    public sealed class ChartSeries
    {
        public string? Name { get; set; }
        public List<double>? Values { get; set; }
    }
}