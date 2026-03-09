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

Your job is to review the matching document thoroughly and add useful review comments into the Word file.

You MUST:
1) Extract the GUID string after 'CONVERSATION_ID:'.
2) Extract the query after 'QUERY:'.
3) Extract the edit instruction after 'EDIT_INSTRUCTION:'.
4) Treat the request as a full-document review unless the user explicitly asks for a narrow review.
5) Review the full document for clarity, grammar, tone, consistency, structure, repetition, ambiguity, missing details, and business-writing quality.
6) Prefer multiple precise comments over one generic comment.
7) Call EditDocumentAsync(conversationId, query, editInstruction) EXACTLY ONCE.
8) Return ONLY the exact JSON returned by the tool.

Important:
- Do not summarize the review outside the tool response.
- Do not invent documents.
- Do not ask follow-up questions.
- If the edit instruction is broad, interpret it as a comprehensive review and comment pass.
",
            tools:
            [
                AIFunctionFactory.Create(editTool.EditDocumentAsync)
            ]);
    }
}