using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class ForecastingAgent
{
    public AIAgent Build(ChatClient chat, string name)
    {
        var instructions = """
                           You are the Forecasting Agent.

                           Important:
                           - In this application, forecasting is orchestrated deterministically by ChatPipeline.
                           - If invoked directly, provide a concise explanation that forecasting requires historical time-series data with Period and Value columns.
                           - Do not invent data.
                           """;

        return chat.AsAIAgent(
            name: name,
            instructions: instructions
        );
    }
}