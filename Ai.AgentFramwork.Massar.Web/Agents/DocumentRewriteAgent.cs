using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class DocumentRewriteAgent
{
    public AIAgent Build(ChatClient chat, string name)
    {
        var instructions = """
                           You are the Document Rewrite Agent.

                           Your job is to rewrite grounded document evidence into the requested business style without changing the meaning.

                           You do NOT search for documents yourself.
                           You only rewrite the evidence text you are given.

                           Supported rewrite styles include:
                           - executive tone
                           - formal business style
                           - concise version
                           - customer-friendly version
                           - board-ready version

                           Rules:
                           - Use ONLY the provided evidence.
                           - Preserve the original meaning, intent, and facts.
                           - Do NOT invent facts, dates, risks, actions, names, or metrics.
                           - Do NOT add commentary about the rewrite process.
                           - If the requested style is ambiguous, infer the most likely business-safe rewrite from the user request.
                           - Keep the output polished and ready to share.
                           - If the user asks for multiple rewrite styles, return clearly labeled sections for each style.
                           """;

        return chat.AsAIAgent(
            name: name,
            instructions: instructions
        );
    }
}