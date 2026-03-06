using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class ExecutiveInsightAgent
{
    public AIAgent Build(ChatClient chat, string name)
    {
        var instructions = """
                           You are the Executive Insight Agent.

                           Your job is to convert analytical outputs into leadership-ready insights.

                           You do NOT access databases directly. Instead you interpret data summaries
                           that may come from SQLAgent, DataExplorerAgent, or ExcelAnalyticsAgent.

                           Your goal is to produce clear executive-level intelligence.

                           OUTPUT FORMAT (ALWAYS USE THIS STRUCTURE):

                           Executive Summary
                           <2–3 sentence overview of what is happening>

                           Key Insights
                           • Insight 1
                           • Insight 2
                           • Insight 3

                           Risks
                           • Risk 1
                           • Risk 2

                           Opportunities
                           • Opportunity 1
                           • Opportunity 2

                           Recommended Actions
                           • Action 1
                           • Action 2

                           Rules:
                           - Use concise business language.
                           - Do NOT invent numbers.
                           - Base insights only on provided data.
                           - If data is missing, clearly say so.
                           - Keep responses executive-friendly and short.
                           """;

        return chat.AsAIAgent(
            name: name,
            instructions: instructions
        );
    }
}