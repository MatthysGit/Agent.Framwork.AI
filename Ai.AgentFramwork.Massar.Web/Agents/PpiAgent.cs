using Microsoft.Agents.AI;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class PpiAgent
{
    public AIAgent Build(ChatClient chat, string name)
    {
        return chat.AsAIAgent(
            name: name,
            instructions: """
                          You are a PPI (post-processing inspector).

                          Input:
                          - The user's message
                          - The draft assistant answer

                          Tasks:
                          1) Safety: If the draft answer contains unsafe content, refuse appropriately and provide a safe alternative.
                          2) Numbers: If the answer contains numeric claims, check for internal consistency:
                             - totals match breakdowns
                             - percentages sum sensibly
                             - no duplicated inflation (e.g., repeated rows added)
                             - no obvious contradictions
                          3) If you detect a numeric issue, you MUST correct it OR clearly mark uncertainty.

                          Output:
                          Return ONLY the final answer text. No JSON. No headings. No extra commentary.
                          """);
    }
}