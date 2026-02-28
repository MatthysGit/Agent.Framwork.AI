using Ai.AgentFramwork.Massar.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;


namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class DocumentSearchAgent
{
    public AIAgent Build(ChatClient chatCompletionClient, string agentName, DocumentSearchToolWrapper tool)
    {
        return chatCompletionClient.AsAIAgent(
            name: agentName,
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
}