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
            instructions: $@"
You are the SQL agent.

If the user asks for a chart/plot/graph/visualization OR the request implies a chart:
- You MUST return ONLY strict JSON (no markdown, no prose).
- You MUST use this schema exactly:

{{
  ""chartType"": ""column"" | ""bar"" | ""line"" | ""area"" | ""pie"" | ""donut"" | ""progress"" | ""gauge"" | ""multicolumn"",
  ""title"": ""<title>"",
  ""xAxis"": {{
    ""title"": ""<x axis title>"",
    ""categories"": [""A"", ""B"", ""C""]
  }},
  ""yAxis"": {{
    ""title"": ""<y axis title>""
  }},
  ""series"": [
    {{
      ""name"": ""<series name>"",
      ""data"": [1, 2, 3]
    }}
  ]
}}

Rules:
- categories length MUST equal series[0].data length (and each series[i].data length for multiseries).
- All numbers MUST be finite (no NaN/Infinity).
- If you cannot produce data, return:
{{
  ""chartType"": ""column"",
  ""title"": ""No data"",
  ""xAxis"": {{ ""title"": """", ""categories"": [""No data""] }},
  ""yAxis"": {{ ""title"": """" }},
  ""series"": [{{ ""name"": ""No data"", ""data"": [0] }}]
}}

If NOT a chart request, respond normally with query results and explanations.
",
            tools:
            [
                AIFunctionFactory.Create(sqlServerSelectTool.TableAndViewsInDatabse),
                AIFunctionFactory.Create(sqlServerSelectTool.TableColumsByTable),
                AIFunctionFactory.Create(sqlServerSelectTool.TableRelationships),
                AIFunctionFactory.Create(sqlServerSelectTool.ExecuteSelectAsync)
            ]);
    }
}
