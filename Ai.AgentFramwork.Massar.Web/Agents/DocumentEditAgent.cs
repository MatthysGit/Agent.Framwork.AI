using Ai.AgentFramwork.Massar.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class DocumentEditAgent
{
    public AIAgent Build(ChatClient chatCompletionClient, string agentName, DocumentEditTool editTool)
    {
        return chatCompletionClient.AsAIAgent(
            name: agentName,
            instructions: @"
You are the Document Edit agent.

INPUT FORMAT (userMessage):
CONVERSATION_ID: <guid>
QUERY: <text>
EDIT_INSTRUCTION: <text>

You MUST:
1) Extract the GUID string after 'CONVERSATION_ID:'.
2) Extract the query after 'QUERY:'.
3) Extract the edit instruction after 'EDIT_INSTRUCTION:'.
4) Call EditDocumentAsync(conversationId, query, editInstruction) EXACTLY ONCE.
5) Return ONLY the exact JSON returned by the tool.
",
            tools:
            [
                AIFunctionFactory.Create(editTool.EditDocumentAsync)
            ]);
    }
}