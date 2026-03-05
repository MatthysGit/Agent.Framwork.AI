using Ai.AgentFramwork.Massar.Web.Tools;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using OpenAI.Chat;
using System.Text.Json;

namespace Ai.AgentFramwork.Massar.Web.Agents;

public sealed class DataExplorerAgent
{

    public AIAgent Build(ChatClient chat, string name, SqlServerSelectTool sqlTool)
    {

        var instructions = """
                           You are a Natural Language Data Exploration Agent.

                           You explore SQL data using the SqlServerSelectTool.

                           CRITICAL TOOL ORDER RULE:
                           1) First call TableAndViewsInDatabse to understand the schema
                           2) Then call GetColumnsForTable or GetRelationships if needed
                           3) Finally execute a safe SELECT query.

                           RULES
                           - Only SELECT queries
                           - Always LIMIT/TOP results
                           - Never modify data
                           - Never assume column names

                           CHART MODE
                           If the user asks for charts return STRICT JSON:

                           {
                            "chartType":"column|bar|line|area|pie|donut|progress|gauge|multicolumn",
                            "title":"string",
                            "xAxis":"column",
                            "yAxis":"column",
                            "series":[]
                           }

                           NON CHART MODE
                           Return:
                           • short explanation
                           • table results
                           • suggested next questions

                           SUGGESTED QUESTIONS
                           Provide 3 follow-up questions for exploration.

                           EXAMPLES
                           User: show employee count by department
                           → column chart

                           User: revenue trend by month
                           → line chart

                           User: top 10 products
                           → bar chart
                           """;

        var tools = new[]
        {
            AIFunctionFactory.Create(sqlTool.ExecuteSelectAsync),
        };
        

        return chat.AsAIAgent(
            name: name,
            instructions: instructions,
            tools: tools);

    }
}