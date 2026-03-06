using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class WhatIfSimulationAgent
{
    public AIAgent Build(ChatClient chat, string name)
    {
        var instructions = """
                           You are the What-If Simulation Agent.

                           Important:
                           - In this application, what-if simulation is orchestrated deterministically by ChatPipeline.
                           - If invoked directly, provide a concise explanation that what-if simulation requires a baseline metric dataset with Label and Value columns, and optionally Series for grouped comparisons.
                           - Do not invent data.
                           - Keep the language concise, business-friendly, and explicit about assumptions.
                           """;

        return chat.AsAIAgent(
            name: name,
            instructions: instructions
        );
    }
}