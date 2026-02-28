using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;
using Ai.AgentFramwork.Massar.Web.Tools;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

#pragma warning disable OPENAI001
public sealed partial class ChatAgentFactory
{
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

        // Tools / deps
        var sqlServerSelectTool = new SqlServerSelectTool((IConfigurationRoot)_configuration, auth);

        // wrappers
        var docSearchToolWrapper = new DocumentSearchToolWrapper(docSearchTool, session);

        // Register specialized agents
        _registry.Register(SqlAgentName, new SqlAgent().Build(chatCompletionClient, SqlAgentName, sqlServerSelectTool));
        _registry.Register(LlmChatAgentName, new GeneralChatAgent().Build(chatCompletionClient, LlmChatAgentName, tools));
        _registry.Register(DocumentSearchAgentName, new DocumentSearchAgent().Build(chatCompletionClient, DocumentSearchAgentName, docSearchToolWrapper));
        _registry.Register(DocumentEditAgentName, new DocumentEditAgent().Build(chatCompletionClient, DocumentEditAgentName, docEditTool));

        // Shared caller for specialists
        var caller = new AgentCallerTool(
            _registry,
            historyProvider: () => ChatMessageWindow.ToSafeTextOnlyMessages(session.Messages, takeLast: 40),
            onRoute: onRoute
        );

        // Router sees history too (same safe transcript window)
        var router = new Ai.AgentFramwork.Massar.Web.Agents.RouterAgent(
            chatCompletionClient,
            historyProvider: () => ChatMessageWindow.ToSafeTextOnlyMessages(session.Messages, takeLast: 40)
        );

        Guid GetConversationIdGuid() => session.ActiveConversationId ?? Guid.Empty;

        return new ChatPipeline(router, caller, tools, docSearchTool, docEditTool, GetConversationIdGuid);
    }
}