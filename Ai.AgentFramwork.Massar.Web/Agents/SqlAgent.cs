using Ai.AgentFramwork.Massar.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class SqlAgent
{
    public AIAgent Build(ChatClient chatCompletionClient, string agentName, SqlServerSelectTool sqlServerSelectTool)
    {
        return chatCompletionClient.AsAIAgent(
            name: agentName,
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
}
