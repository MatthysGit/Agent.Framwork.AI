using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Threading;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

public sealed class AiUsageContext
{
    public string? UserId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? MessageId { get; set; }
    public string? AgentName { get; set; }
    public string? ModelName { get; set; }
    public string? ClientRequestId { get; set; }

    public AiUsageContext Clone() => new()
    {
        UserId = UserId,
        ConversationId = ConversationId,
        MessageId = MessageId,
        AgentName = AgentName,
        ModelName = ModelName,
        ClientRequestId = ClientRequestId
    };
}

public interface IAiUsageContextAccessor
{
    AiUsageContext GetCurrent();
    void SetUserAndConversation(string? userId, Guid? conversationId, Guid? messageId = null);
    void SetAgent(string? agentName, string? modelName = null);
    void SetClientRequestId(string? clientRequestId);
    void ClearTurn();
}

public sealed class AiUsageContextAccessor : IAiUsageContextAccessor
{
    private readonly AsyncLocal<AiUsageContext?> _current = new();

    public AiUsageContext GetCurrent()
    {
        _current.Value ??= new AiUsageContext();
        return _current.Value;
    }

    public void SetUserAndConversation(string? userId, Guid? conversationId, Guid? messageId = null)
    {
        var ctx = GetCurrent();
        ctx.UserId = userId;
        ctx.ConversationId = conversationId;
        ctx.MessageId = messageId;
    }

    public void SetAgent(string? agentName, string? modelName = null)
    {
        var ctx = GetCurrent();
        ctx.AgentName = agentName;
        if (!string.IsNullOrWhiteSpace(modelName))
            ctx.ModelName = modelName;
    }

    public void SetClientRequestId(string? clientRequestId)
    {
        GetCurrent().ClientRequestId = clientRequestId;
    }

    public void ClearTurn() => _current.Value = null;
}

public sealed record AiUsageLogRequest(
    string AgentName,
    string ModelName,
    string? UserId,
    Guid? ConversationId,
    Guid? MessageId,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int? CachedInputTokens,
    int? ReasoningTokens,
    string? RequestId,
    string? ClientRequestId,
    int? LatencyMs,
    bool Succeeded,
    string? ErrorMessage);

public interface IAiUsageLogger
{
    Task LogAsync(AiUsageLogRequest request, CancellationToken ct = default);
}

public sealed class AiUsageLogger : IAiUsageLogger
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AiUsageLogger>? _logger;

    public AiUsageLogger(IDbContextFactory<AppDbContext> dbFactory, ILogger<AiUsageLogger>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task LogAsync(AiUsageLogRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || request.ConversationId is null || string.IsNullOrWhiteSpace(request.AgentName) || string.IsNullOrWhiteSpace(request.ModelName))
            return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var pricing = await db.AiPricings
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ModelName == request.ModelName && x.IsActive, ct);

            decimal? inputCost = null;
            decimal? outputCost = null;
            decimal? totalCost = null;

            if (pricing is not null)
            {
                var prompt = Math.Max(0, request.PromptTokens ?? 0);
                var completion = Math.Max(0, request.CompletionTokens ?? 0);
                var cached = Math.Max(0, request.CachedInputTokens ?? 0);

                var baseInput = (prompt / 1_000_000m) * pricing.InputCostPer1M;
                var cachedSavings = pricing.CachedInputCostPer1M.HasValue
                    ? (cached / 1_000_000m) * Math.Max(0m, pricing.InputCostPer1M - pricing.CachedInputCostPer1M.Value)
                    : 0m;

                inputCost = Math.Max(0m, baseInput - cachedSavings);
                outputCost = (completion / 1_000_000m) * pricing.OutputCostPer1M;
                totalCost = inputCost.Value + outputCost.Value;
            }

            db.AiTokenUsageLogs.Add(new AiTokenUsageLog
            {
                AiTokenUsageLogId = Guid.NewGuid(),
                ConversationId = request.ConversationId.Value,
                UserId = request.UserId!,
                MessageId = request.MessageId,
                AgentName = request.AgentName,
                ModelName = request.ModelName,
                PromptTokens = request.PromptTokens,
                CompletionTokens = request.CompletionTokens,
                TotalTokens = request.TotalTokens ?? SumNullable(request.PromptTokens, request.CompletionTokens),
                CachedInputTokens = request.CachedInputTokens,
                ReasoningTokens = request.ReasoningTokens,
                InputCostUsd = inputCost,
                OutputCostUsd = outputCost,
                TotalCostUsd = totalCost,
                RequestId = TrimOrNull(request.RequestId, 200),
                ClientRequestId = TrimOrNull(request.ClientRequestId, 200),
                LatencyMs = request.LatencyMs,
                Succeeded = request.Succeeded,
                ErrorMessage = request.ErrorMessage,
                CreatedOn = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to write AI token usage log for {AgentName}/{ModelName}.", request.AgentName, request.ModelName);
        }
    }

    private static int? SumNullable(int? a, int? b)
        => a.HasValue || b.HasValue ? (a ?? 0) + (b ?? 0) : null;

    private static string? TrimOrNull(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

public sealed record AiBudgetCheckResult(bool Allowed, decimal CurrentMonthSpendUsd, decimal? MonthlyLimitUsd, decimal? RemainingUsd);

public interface IAiBudgetGuardService
{
    Task<AiBudgetCheckResult> CheckAsync(string userId, CancellationToken ct = default);
}

public sealed class AiBudgetGuardService : IAiBudgetGuardService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public AiBudgetGuardService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<AiBudgetCheckResult> CheckAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return new AiBudgetCheckResult(true, 0m, null, null);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

        var spend = await db.AiTokenUsageLogs
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.CreatedOn >= monthStart)
            .SumAsync(x => (decimal?)x.TotalCostUsd, ct) ?? 0m;

        var limit = await db.AiUserMonthlyCostLimits
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.IsActive, ct);

        if (limit is null)
            return new AiBudgetCheckResult(true, spend, null, null);

        var remaining = limit.MonthlyLimitUsd - spend;
        return new AiBudgetCheckResult(spend < limit.MonthlyLimitUsd, spend, limit.MonthlyLimitUsd, remaining);
    }
}

public static class AiTokenEstimator
{
    public static int EstimateTextTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }

    public static int EstimateMessagesTokens(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += 6;
            total += EstimateTextTokens(string.Concat(message.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(x => x.Text)));
        }
        return total;
    }
}

public sealed record AiUsageSnapshot(
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int? CachedInputTokens,
    int? ReasoningTokens,
    string? RequestId);

public static class AiUsageReflection
{
    public static AiUsageSnapshot ExtractSnapshot(object? response)
    {
        if (response is null)
            return new AiUsageSnapshot(null, null, null, null, null, null);

        var value = GetProperty(response, "Value") ?? response;
        var usage = GetProperty(value, "Usage") ?? GetProperty(response, "Usage");
        var details = GetProperty(usage, "InputTokenDetails") ?? GetProperty(usage, "InputTokensDetails") ?? GetProperty(usage, "OutputTokenDetails") ?? GetProperty(usage, "OutputTokensDetails");

        var prompt = GetNullableInt(usage, "InputTokenCount")
                     ?? GetNullableInt(usage, "InputTokens")
                     ?? GetNullableInt(usage, "PromptTokenCount")
                     ?? GetNullableInt(usage, "PromptTokens");

        var completion = GetNullableInt(usage, "OutputTokenCount")
                         ?? GetNullableInt(usage, "OutputTokens")
                         ?? GetNullableInt(usage, "CompletionTokenCount")
                         ?? GetNullableInt(usage, "CompletionTokens");

        var total = GetNullableInt(usage, "TotalTokenCount")
                    ?? GetNullableInt(usage, "TotalTokens");

        var cached = GetNullableInt(details, "CachedTokenCount")
                     ?? GetNullableInt(details, "CachedInputTokenCount")
                     ?? GetNullableInt(details, "CachedTokens")
                     ?? GetNullableInt(usage, "CachedInputTokens");

        var reasoning = GetNullableInt(details, "ReasoningTokenCount")
                        ?? GetNullableInt(details, "ReasoningTokens")
                        ?? GetNullableInt(usage, "ReasoningTokens");

        var requestId = GetString(response, "RequestId")
                        ?? GetString(value, "Id")
                        ?? GetString(response, "Id");

        return new AiUsageSnapshot(prompt, completion, total, cached, reasoning, requestId);
    }

    private static object? GetProperty(object? target, string propertyName)
        => target?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

    private static int? GetNullableInt(object? target, string propertyName)
    {
        var value = GetProperty(target, propertyName);
        if (value is null) return null;
        return value switch
        {
            int i => i,
            long l => checked((int)l),
            short s => s,
            byte b => b,
            _ => int.TryParse(value.ToString(), out var parsed) ? parsed : null
        };
    }

    private static string? GetString(object? target, string propertyName)
    {
        var value = GetProperty(target, propertyName)?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
