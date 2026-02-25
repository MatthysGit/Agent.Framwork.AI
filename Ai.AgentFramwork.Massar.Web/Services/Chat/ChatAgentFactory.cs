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

    private readonly IConfiguration _configuration;
    private readonly IAgentRegistry _registry;
    private readonly IServiceProvider _services;

    public ChatAgentFactory(IConfiguration configuration, IAgentRegistry registry, IServiceProvider services)
    {
        _configuration = configuration;
        _registry = registry;
        _services = services;
    }

    private sealed class DocumentSearchToolWrapper
    
    
    {
        private readonly DocumentSearchTool _inner;
        private readonly ChatSession _session;


        public DocumentSearchToolWrapper(DocumentSearchTool inner,ChatSession session)
        {
            _inner = inner;
            _session = session;
            
        }

        [Description("Search documents for a conversation. conversationId MUST be a GUID string. Returns JSON.")]
        public Task<string> SearchDocumentsAsync(
            [Description("Conversation Id as GUID string")] string conversationId,
            [Description("Search query")] string query)
        {
            


            if (!Guid.TryParse(_session.ActiveConversationId!.Value.ToString(), out var convoId))
                return Task.FromResult(@"{""Text"":""Invalid conversation id."",""Attachments"":[]}");

            return _inner.SearchDocumentsAsync(convoId, query);
        }
    }
    
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




        // Register specialized agents
        _registry.Register(SqlAgentName, BuildSqlAgent(chatCompletionClient, sqlServerSelectTool));
        _registry.Register(LlmChatAgentName, BuildGeneralChatAgent(chatCompletionClient, tools));
        _registry.Register(DocumentSearchAgentName, BuildDocumentSearchAgent(chatCompletionClient, docSearchToolWrapper));

        // Orchestrator calls agents via AgentCallerTool
        var caller = new AgentCallerTool(
            _registry,
            historyProvider: () => ChatMessageWindow.ToSafeTextOnlyMessages(session.Messages, takeLast: 40),
            onRoute: _ => { /* optional */ });

        // Orchestrator tool: provide the current conversation id
        Task<string> GetConversationId()
            => Task.FromResult(session.ActiveConversationId?.ToString() ?? "");

        return chatCompletionClient.AsAIAgent(
            name: "OrchestratorAgent",
            instructions: $@"You are the orchestrator.

Your job:
1) Decide which specialized agent should handle the user's request.
2) Call that agent using the CallAgentAsync tool.
3) If the user requested a chart that depends on SQL data, you MUST fetch data via {SqlAgentName} and then generate a chart image using the chart tool.
4) Otherwise, return the agent's answer EXACTLY as provided.

Available agents:
1) {DocumentSearchAgentName} - search uploaded documents (global + scoped chat attachments) and return JSON.
2) {SqlAgentName} - SQL / database questions and anything requiring SELECT queries.
3) {LlmChatAgentName} - general conversation and everything else.

DOCUMENT CALL FORMAT (MANDATORY):
- Before calling {DocumentSearchAgentName}, you MUST call GetConversationId.
- Then call {DocumentSearchAgentName} with the userMessage EXACTLY formatted as:
  CONVERSATION_ID: <guid>
  QUERY: <the user's original question>

ABSOLUTE DOCUMENT ROUTING RULE (MUST FOLLOW):
- If the user's message OR recent chat context contains ANY of the following, you MUST call {DocumentSearchAgentName}:
  - file links like '/api/chat/attachments/' or '/documents/files/download/'
  - file extensions/keywords: pdf, doc, docx, txt, csv, xls, xlsx
  - phrases indicating document lookup: 'according to', 'in the document', 'in the pdf', 'from the file',
    'what is included', 'what does it say', 'summarize the document', 'quote', 'cite'

ROUTING PROCEDURE (MUST FOLLOW IN ORDER):
0) If the user asks for a chart/plot/graph/visualization AND the request requires ANY SQL/database query
   (examples: 'from the database', 'dbo.', 'SELECT', 'COUNT', 'SUM', 'GROUP BY', table/view names):
   0.1) Call {SqlAgentName} using CallAgentAsync.
   0.2) The SQL agent MUST return ONLY JSON in one of the supported schemas below (no markdown, no prose).

   0.3) Chart tool mapping (call exactly one):
        - chartType ""bar""   => CreateBarChartPngAsync(title, xAxisLabel, yAxisLabel, labels, values)
        - chartType ""pie""   => CreatePieChartPngAsync(title, labels, values)
        - chartType ""line""  => CreateLineChartPngAsync(title, xAxisLabel, yAxisLabel, labels, values)
        - chartType ""area""  => CreateAreaChartPngAsync(title, xAxisLabel, yAxisLabel, labels, values)
        - chartType ""donut"" => CreateDonutChartPngAsync(title, labels, values)
        - chartType ""gauge"" => CreateGaugeChartPngAsync(title, value, min, max)
        - chartType ""progress"" => CreateProgressBarsChartPngAsync(title, labels, values)
        - chartType ""multicolumn"" => CreateMultiSeriesColumnChartPngAsync(title, xAxisLabel, yAxisLabel, labels, series)

   0.4) Return ONLY the markdown image:
        ![chart](URL)

1) If the request is clearly SQL/database (no chart) => call {SqlAgentName} using CallAgentAsync.
2) If the request is document-related per the ABSOLUTE DOCUMENT ROUTING RULE => call {DocumentSearchAgentName} using the DOCUMENT CALL FORMAT.
3) Otherwise call {LlmChatAgentName} using CallAgentAsync.

Supported JSON schemas from {SqlAgentName}:

Single-series charts:
{{
  ""chartType"": ""bar"" | ""pie"" | ""line"" | ""area"" | ""donut"",
  ""title"": ""..."",
  ""xAxisLabel"": ""..."",
  ""yAxisLabel"": ""..."",
  ""labels"": [""A"", ""B""],
  ""values"": [123, 456]
}}

Multi-series column chart:
{{
  ""chartType"": ""multicolumn"",
  ""title"": ""..."",
  ""xAxisLabel"": ""..."",
  ""yAxisLabel"": ""..."",
  ""labels"": [""A"", ""B""],
  ""series"": [
    {{ ""name"": ""Series1"", ""values"": [10, 20] }},
    {{ ""name"": ""Series2"", ""values"": [5, 7] }}
  ]
}}

Gauge chart:
{{
  ""chartType"": ""gauge"",
  ""title"": ""..."",
  ""value"": 75,
  ""min"": 0,
  ""max"": 100
}}

Progress bars chart:
{{
  ""chartType"": ""progress"",
  ""title"": ""..."",
  ""labels"": [""A"", ""B""],
  ""values"": [40, 80]
}}

CRITICAL OUTPUT RULES:
- After calling CallAgentAsync, you will receive an object with fields AgentName and Text.
- If you generated a chart image using a chart tool, return ONLY: ![chart](URL)
- Otherwise you MUST return ONLY the Text field.
- Do NOT add explanations.
- Do NOT summarize.
- Do NOT rewrite.",
            tools:
            [
                AIFunctionFactory.Create(caller.CallAgentAsync),
                AIFunctionFactory.Create(GetConversationId),
                AIFunctionFactory.Create(GetCurrentTime),

                // Chart tools available to orchestrator
                AIFunctionFactory.Create(tools.CreateBarChartPngAsync),
                AIFunctionFactory.Create(tools.CreatePieChartPngAsync),
                AIFunctionFactory.Create(tools.CreateLineChartPngAsync),
                AIFunctionFactory.Create(tools.CreateAreaChartPngAsync),
                AIFunctionFactory.Create(tools.CreateDonutChartPngAsync),
                AIFunctionFactory.Create(tools.CreateGaugeChartPngAsync),
                AIFunctionFactory.Create(tools.CreateProgressBarsChartPngAsync),
                AIFunctionFactory.Create(tools.CreateMultiSeriesColumnChartPngAsync),
            ]);
    }

    private AIAgent BuildDocumentSearchAgent(ChatClient chatCompletionClient, DocumentSearchToolWrapper tool)
    {
        return chatCompletionClient.AsAIAgent(
            name: DocumentSearchAgentName,
            instructions: @"
You are the Document Search agent.

INPUT FORMAT (userMessage):
CONVERSATION_ID: <guid>
QUERY: <text>

You MUST:
1) Extract the GUID string after 'CONVERSATION_ID:'.
2) Extract the query after 'QUERY:'.
3) Call SearchDocumentsAsync(conversationId, query) EXACTLY ONCE.
4) Return ONLY the exact JSON returned by the tool.
",
            tools:
            [
                AIFunctionFactory.Create(tool.SearchDocumentsAsync)
            ]);
    }

    private static AIAgent BuildSqlAgent(ChatClient chatCompletionClient, SqlServerSelectTool sqlServerSelectTool)
    {
        return chatCompletionClient.AsAIAgent(
            name: SqlAgentName,
            instructions: @$"You are a SQL specialist for data-related questions.

CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.

If the user asks for a chart/graph/plot/visualization:
- Run the SELECT query needed to produce the chart data.
- Return ONLY JSON (no markdown, no prose) in ONE of the supported shapes.

Otherwise (no chart requested), return query results in a markdown table.

Authorization rules and query rules unchanged.",
            tools:
            [
                AIFunctionFactory.Create(sqlServerSelectTool.TableAndViewsInDatabse),
                AIFunctionFactory.Create(sqlServerSelectTool.TableColumsByTable),
                AIFunctionFactory.Create(sqlServerSelectTool.TableRelationships),
                AIFunctionFactory.Create(sqlServerSelectTool.ExecuteSelectAsync)
            ]);
    }

    private static AIAgent BuildGeneralChatAgent(ChatClient chatCompletionClient, ChatTools tools)
    {
        return chatCompletionClient.AsAIAgent(
            name: LlmChatAgentName,
            instructions: @"You are a helpful general chat assistant.
Use simple markdown.

When the user asks for current facts, rankings, statistics, recent news, or anything time-sensitive, use WebSearchAsync to ground your answer.

CHARTS (MANDATORY):
- If the user asks for ANY chart/graph/plot, you MUST generate an IMAGE.
- Never output a textual chart specification.
- For BAR charts: call CreateBarChartPngAsync with title, xAxisLabel, yAxisLabel, labels[], values[].
- Then return ONLY markdown image:
  ![chart](URL)
- If you cannot call the tool for any reason, reply EXACTLY: TOOL_CALL_FAILED

Chart tool mapping:
- Pie chart => CreatePieChartPngAsync
- Bar chart => CreateBarChartPngAsync",
            tools:
            [
                AIFunctionFactory.Create(tools.WebSearchAsync),
                AIFunctionFactory.Create(tools.CreatePieChartPngAsync),
                AIFunctionFactory.Create(tools.CreateBarChartPngAsync),
                AIFunctionFactory.Create(tools.CreateChatFileAsync)
            ]);
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

