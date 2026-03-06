using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class AnomalyDetectionAgent
{
    public AIAgent Build(ChatClient chat, string name)
    {
        var instructions = """
                           You are the Anomaly Detection Agent.

                           Important:
                           - In this application, anomaly detection is orchestrated deterministically by ChatPipeline.
                           - If invoked directly, provide a concise explanation that anomaly detection requires historical time-series or grouped metric data with Period and Value columns.
                           - Do not invent data.
                           - Prefer concise business language.
                           """;

        return chat.AsAIAgent(
            name: name,
            instructions: instructions
        );
    }
}