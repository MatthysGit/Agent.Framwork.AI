using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class GeneralChatAgent
{
    public AIAgent Build(ChatClient chatCompletionClient, string agentName, ChatTools tools)
    {
        return chatCompletionClient.AsAIAgent(
            name: agentName,
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
}
