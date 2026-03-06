// File: Services/Chat/ChatAgentFactory.cs
using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Services.Chat.DecisionTracking;
using Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;
using Ai.AgentFramwork.Massar.Web.Tools;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using System.ComponentModel;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

#pragma warning disable OPENAI001
public sealed class ChatAgentFactory
{
    public const string DocumentSearchAgentName = "DocumentSearchAgent";
    public const string SqlAgentName = "SqlAgent";
    public const string LlmChatAgentName = "LlmChatAgent";
    public const string DocumentEditAgentName = "DocumentEditAgent";
    public const string OrchestratorAgentName = "OrchestratorAgent";
    public const string PpiAgentName = "PpiAgent";
    public const string ExcelAnalyticsAgentName = "ExcelAnalyticsAgent";
    public const string DataExplorerAgentName = "DataExplorerAgent";
    public const string ExecutiveInsightAgentName = "ExecutiveInsightAgent";
    public const string ForecastingAgentName = "ForecastingAgent";
    public const string AnomalyDetectionAgentName = "AnomalyDetectionAgent";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IConfiguration _configuration;
    private readonly IAgentRegistry _registry;

    // ✅ runtime model + client creation
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IAgentModelSelector _modelSelector;

    // ✅ used to create logger without having a _decisionLogger field
    private readonly ILoggerFactory _loggerFactory;

    public ChatAgentFactory(
        IConfiguration configuration,
        IAgentRegistry registry,
        IDbContextFactory<AppDbContext> dbFactory,
        IChatClientFactory chatClientFactory,
        IAgentModelSelector modelSelector,
        ILoggerFactory loggerFactory) // ✅ add this instead of ILogger<DecisionTrackerService>
    {
        _configuration = configuration;
        _registry = registry;
        _dbFactory = dbFactory;

        _chatClientFactory = chatClientFactory;
        _modelSelector = modelSelector;

        _loggerFactory = loggerFactory;
    }

    // --- Pipeline builder (AI Router + deterministic execution) ---
    public ChatPipeline BuildPipeline(
        ChatTools tools,
        ChatSession session,
        IDbContextFactory<AppDbContext> dbFactory,
        AuthenticationStateProvider auth,
        DocumentSearchTool docSearchTool,
        DocumentEditTool docEditTool,
        IDecisionTrackerService? decisionTracker = null,   // ✅ optional
        Action<string>? onRoute = null,
        Func<string?>? getOwnerUserId = null)
    {
        var sqlServerSelectTool = new SqlServerSelectTool((IConfigurationRoot)_configuration, auth);
        var docSearchToolWrapper = new DocumentSearchToolWrapper(docSearchTool, session);

        // ✅ Register specialized agent BUILDERS (not pre-built agents)
        _registry.Register(SqlAgentName, (sp, chatClient) =>
            new SqlAgent().Build(chatClient, SqlAgentName, sqlServerSelectTool));

        _registry.Register(LlmChatAgentName, (sp, chatClient) =>
            new GeneralChatAgent().Build(chatClient, LlmChatAgentName, tools));

        _registry.Register(DocumentSearchAgentName, (sp, chatClient) =>
            new DocumentSearchAgent().Build(chatClient, DocumentSearchAgentName, docSearchToolWrapper));

        _registry.Register(DocumentEditAgentName, (sp, chatClient) =>
            new DocumentEditAgent().Build(chatClient, DocumentEditAgentName, docEditTool));

        _registry.Register(PpiAgentName, (sp, chatClient) =>
            new PpiAgent().Build(chatClient, PpiAgentName));


        _registry.Register(ExcelAnalyticsAgentName, (sp, chatClient) =>
        {
            var excelTool = sp.GetRequiredService<ExcelAnalyticsToolWrapper>();
            return new ExcelAnalyticsAgent().Build(chatClient, ExcelAnalyticsAgentName, excelTool);
        });


        _registry.Register(DataExplorerAgentName, (sp, chatClient) =>
            new SqlAgent().Build(chatClient, DataExplorerAgentName, sqlServerSelectTool));


        _registry.Register(ExecutiveInsightAgentName, (sp, chatClient) =>
            new ExecutiveInsightAgent().Build(chatClient, ExecutiveInsightAgentName));

        _registry.Register(ForecastingAgentName, (sp, chatClient) =>
            new ForecastingAgent().Build(chatClient, ForecastingAgentName));

        _registry.Register(AnomalyDetectionAgentName, (sp, chatClient) =>
            new AnomalyDetectionAgent().Build(chatClient, AnomalyDetectionAgentName));


        // ✅ Caller chooses model at runtime per agent
        var caller = new AgentCallerTool(
            _registry,
            _modelSelector,
            historyProvider: () => ChatMessageWindow.ToSafeTextOnlyMessages(session.Messages, takeLast: 40),
            onRoute: onRoute
        );

        // ✅ Build DecisionTracker here if not supplied (caller is not DI-registered)
        if (decisionTracker is null)
        {
            var logger = _loggerFactory.CreateLogger<DecisionTrackerService>();
            decisionTracker = new DecisionTrackerService(caller, logger);
        }

        // ✅ Router uses its own model (runtime)
        var routerModelKey = _modelSelector.GetModelForAgent(OrchestratorAgentName);
        ChatClient routerClient = _chatClientFactory.Create(routerModelKey);

        var router = new RouterAgent(
            routerClient,
            historyProvider: () => ChatMessageWindow.ToSafeTextOnlyMessages(session.Messages, takeLast: 40)
        );

        Guid GetConversationIdGuid() => session.ActiveConversationId ?? Guid.Empty;
        Task<bool> IsPrivileged() => IsPrivilegedAsync(auth, _dbFactory);

        return new ChatPipeline(
            router,
            caller,
            tools,
            docSearchTool,
            docEditTool,
            GetConversationIdGuid,
            canViewCompensationAsync: () => IsPrivileged(),
            isPrivilegedAsync: () => IsPrivileged(),
            decisionTracker: decisionTracker,
            getOwnerUserId: getOwnerUserId
        );
    }

    // ✅ EF-BACKED PRIVILEGE CHECK
    private static async Task<bool> IsPrivilegedAsync(
        AuthenticationStateProvider auth,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        var state = await auth.GetAuthenticationStateAsync();
        var user = state.User;

        var roleIds = user.Claims
            .Where(c => c.Type is "roleId" or "RoleId" or System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value)
            .Select(v => int.TryParse(v, out var i) ? (int?)i : null)
            .Where(i => i != null)
            .Select(i => i!.Value)
            .Distinct()
            .ToArray();

        if (roleIds.Length == 0)
            return false;

        await using var db = await dbFactory.CreateDbContextAsync();

        return await db.Roles
            .AsNoTracking()
            .AnyAsync(r => r.PrivilegedPPI == true && roleIds.Contains(r.RoleId));
    }

    [Description("Get the current date and time")]
    private static Task<string> GetCurrentTime()
        => Task.FromResult(DateTime.Now.ToString());
}

/// <summary>Small helper to keep the safe-window logic out of ChatService.</summary>
internal static class ChatMessageWindow
{
    public static List<ChatMessage> ToSafeTextOnlyMessages(IEnumerable<ChatMessage> source, int takeLast)
    {
        var window = source as IList<ChatMessage> ?? source.ToList();

        if (takeLast > 0 && window.Count > takeLast)
            window = window.Skip(window.Count - takeLast).ToList();

        var safe = new List<ChatMessage>();

        foreach (var m in window)
        {
            if (m.Role != ChatRole.User && m.Role != ChatRole.Assistant)
                continue;

            var text = string.Concat(m.Contents.OfType<TextContent>().Select(t => t.Text));
            if (string.IsNullOrWhiteSpace(text))
                continue;

            safe.Add(new ChatMessage(m.Role, new[] { new TextContent(text) }));
        }

        return safe;
    }
}