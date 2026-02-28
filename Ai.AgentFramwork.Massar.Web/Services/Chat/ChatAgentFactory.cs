using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
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

    private readonly IConfiguration _configuration;
    private readonly IAgentRegistry _registry;
    private readonly IServiceProvider _services;

    public ChatAgentFactory(IConfiguration configuration, IAgentRegistry registry, IServiceProvider services)
    {
        _configuration = configuration;
        _registry = registry;
        _services = services;
    }

    public AIAgent BuildOrchestratorAgent(
        ChatTools tools,
        ChatSession session,
        IDbContextFactory<AppDbContext> dbFactory,
        AuthenticationStateProvider auth)
    {
        var client = new OpenAIClient(_configuration["OpenAI:Key"] ?? "");
        var chatCompletionClient = client.GetChatClient("gpt-4.1");

        // Tools / deps
        var sqlServerSelectTool = new SqlServerSelectTool((IConfigurationRoot)_configuration, auth);

        var docSearchTool = _services.GetRequiredService<DocumentSearchTool>();
        var docSearchToolWrapper = new DocumentSearchToolWrapper(docSearchTool, session);

        var docEditTool = _services.GetRequiredService<DocumentEditTool>();

        // Build + Register specialized agents (each agent lives in its own class now)
        _registry.Register(SqlAgentName, new SqlAgent().Build(chatCompletionClient, SqlAgentName, sqlServerSelectTool));
        _registry.Register(LlmChatAgentName, new GeneralChatAgent().Build(chatCompletionClient, LlmChatAgentName, tools));
        _registry.Register(DocumentSearchAgentName, new DocumentSearchAgent().Build(chatCompletionClient, DocumentSearchAgentName, docSearchToolWrapper));
        _registry.Register(DocumentEditAgentName, new DocumentEditAgent().Build(chatCompletionClient, DocumentEditAgentName, docEditTool));

        // Orchestrator calls agents via AgentCallerTool
        var caller = new AgentCallerTool(
            _registry,
            historyProvider: () => ChatMessageWindow.ToSafeTextOnlyMessages(session.Messages, takeLast: 40),
            onRoute: route => Console.WriteLine($"[OrchestratorRoute] {route}")
        );

        Task<string> GetConversationId()
            => Task.FromResult(session.ActiveConversationId?.ToString() ?? "");

        // Orchestrator agent moved to its own class as well
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