using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;
using Ai.AgentFramwork.Massar.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
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
    
    private readonly IConfiguration _configuration;
    private readonly IAgentRegistry _registry;
    private readonly IServiceProvider _services;

    public ChatAgentFactory(IConfiguration configuration, IAgentRegistry registry, IServiceProvider services)
    {
        _configuration = configuration;
        _registry = registry;
        _services = services;
    }

    // --- OLD (optional): keep if you still want orchestrator path for comparison/tests ---
    public AIAgent BuildOrchestratorAgent(
        ChatTools tools,
        ChatSession session,
        IDbContextFactory<AppDbContext> dbFactory,
        AuthenticationStateProvider auth)
    {
        var client = new OpenAIClient(_configuration["OpenAI:Key"] ?? "");
        var chatCompletionClient = client.GetChatClient("gpt-4.1");

        var sqlServerSelectTool = new SqlServerSelectTool((IConfigurationRoot)_configuration, auth);

        var docSearchTool = _services.GetRequiredService<DocumentSearchTool>();
        var docSearchToolWrapper = new DocumentSearchToolWrapper(docSearchTool, session);

        var docEditTool = _services.GetRequiredService<DocumentEditTool>();

        _registry.Register(SqlAgentName, new SqlAgent().Build(chatCompletionClient, SqlAgentName, sqlServerSelectTool));
        _registry.Register(LlmChatAgentName, new GeneralChatAgent().Build(chatCompletionClient, LlmChatAgentName, tools));
        _registry.Register(DocumentSearchAgentName, new DocumentSearchAgent().Build(chatCompletionClient, DocumentSearchAgentName, docSearchToolWrapper));
        _registry.Register(DocumentEditAgentName, new DocumentEditAgent().Build(chatCompletionClient, DocumentEditAgentName, docEditTool));
        _registry.Register(PpiAgentName, new PpiAgent().Build(chatCompletionClient, PpiAgentName));
        
        var caller = new AgentCallerTool(
            _registry,
            historyProvider: () => ChatMessageWindow.ToSafeTextOnlyMessages(session.Messages, takeLast: 40),
            onRoute: route => Console.WriteLine($"[OrchestratorRoute] {route}")
        );

        Task<string> GetConversationId()
            => Task.FromResult(session.ActiveConversationId?.ToString() ?? "");

        return new OrchestratorAgent().Build(
            chatCompletionClient,
            OrchestratorAgentName,
            caller,
            tools,
            GetConversationId,
            SqlAgentName,
            DocumentSearchAgentName,
            DocumentEditAgentName,
            LlmChatAgentName);
    }

    // --- NEW: Pipeline builder (AI Router + deterministic execution) ---
    public ChatPipeline BuildPipeline(
        ChatTools tools,
        ChatSession session,
        IDbContextFactory<AppDbContext> dbFactory,
        AuthenticationStateProvider auth,
        DocumentSearchTool docSearchTool,
        DocumentEditTool docEditTool,
        Action<string>? onRoute = null)
    {
        var client = new OpenAIClient(_configuration["OpenAI:Key"] ?? "");
        var chatCompletionClient = client.GetChatClient("gpt-4.1");

        var sqlServerSelectTool = new SqlServerSelectTool((IConfigurationRoot)_configuration, auth);

        var docSearchToolWrapper = new DocumentSearchToolWrapper(docSearchTool, session);

        // Register specialized agents
        _registry.Register(SqlAgentName, new SqlAgent().Build(chatCompletionClient, SqlAgentName, sqlServerSelectTool));
        _registry.Register(LlmChatAgentName, new GeneralChatAgent().Build(chatCompletionClient, LlmChatAgentName, tools));
        _registry.Register(DocumentSearchAgentName, new DocumentSearchAgent().Build(chatCompletionClient, DocumentSearchAgentName, docSearchToolWrapper));
        _registry.Register(DocumentEditAgentName, new DocumentEditAgent().Build(chatCompletionClient, DocumentEditAgentName, docEditTool));
        _registry.Register(PpiAgentName, new PpiAgent().Build(chatCompletionClient, PpiAgentName));
        
        // Shared caller for specialists
        var caller = new AgentCallerTool(
            _registry,
            historyProvider: () => ChatMessageWindow.ToSafeTextOnlyMessages(session.Messages, takeLast: 40),
            onRoute: onRoute
        );

        // AI Router (uses same chat client) + sees safe transcript too
        var router = new Ai.AgentFramwork.Massar.Web.Agents.RouterAgent(
            chatCompletionClient,
            historyProvider: () => ChatMessageWindow.ToSafeTextOnlyMessages(session.Messages, takeLast: 40)
        );

        Guid GetConversationIdGuid() => session.ActiveConversationId ?? Guid.Empty;
        //IsPrivilegedAsync
        Task<bool> IsRole3Async() => IsPrivilegedAsync(auth);
        
        return new ChatPipeline(router, caller, tools, docSearchTool, docEditTool, GetConversationIdGuid, canViewCompensationAsync: () => IsRole3Async(), isPrivilegedAsync: () => IsRole3Async());
    }

    private static async Task<bool> IsPrivilegedAsync(AuthenticationStateProvider auth)
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

        return roleIds.Contains(3);
    }
    

    private static async Task<bool> CanViewCompensationAsync(AuthenticationStateProvider auth)
    {
        var state = await auth.GetAuthenticationStateAsync();
        var user = state.User;

        // Adjust to your real claims/roles:
        // Example: role claim contains "HR" or "Payroll"
        return user.IsInRole("HR") || user.IsInRole("Payroll") ||
               user.Claims.Any(c => c.Type == "permission" && c.Value.Equals("compensation.read", StringComparison.OrdinalIgnoreCase));
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