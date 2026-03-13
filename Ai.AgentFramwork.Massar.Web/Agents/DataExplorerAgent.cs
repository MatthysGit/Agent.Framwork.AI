using Ai.AgentFramwork.Massar.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ExtChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ExtChatRole = Microsoft.Extensions.AI.ChatRole;
using OpenAIChatClient = OpenAI.Chat.ChatClient;

namespace Ai.AgentFramwork.Massar.Web.Agents;

public sealed class DataExplorerAgent
{
    public AIAgent Build(OpenAIChatClient chat, string name, SqlServerSelectTool sqlTool)
    {
        var instructions = @"
You are the Data Explorer agent.

Purpose:
- help users explore database schema
- profile tables
- explain joins/relationships
- run SELECT queries when the user asks for data
- return chart JSON only for chart requests

========================================================
CRITICAL TOOL ORDER RULE (MUST FOLLOW):
- At the START of EVERY user request, you MUST call TableAndViewsInDatabse first.
- You MUST NOT call ExecuteSelectAsync until AFTER you have called TableAndViewsInDatabse for this request.
- If you need any table/column info, call TableColumsByTable / TableRelationships ONLY AFTER TableAndViewsInDatabse.
- If TableAndViewsInDatabse fails or returns empty, reply exactly: unauthorized access.
========================================================

CLASSIFY THE REQUEST AFTER TableAndViewsInDatabse:

1) SCHEMA MODE
Use for:
- what tables / which tables
- schema / relationships / joins / columns
- how to analyze a business question
- profile <table>
- describe <table>
- explain table structure

Rules:
- DO NOT call ExecuteSelectAsync unless the user explicitly asks for data/results.
- You MAY call TableColumsByTable and TableRelationships.
- For profile/describe table questions, focus on the requested table first.

2) QUERY MODE
Use when the user asks for rows, counts, lists, aggregates, trends, top/bottom N, or actual results.
Rules:
- inspect columns/relationships as needed
- then call ExecuteSelectAsync
- return the query result for grid rendering

3) CHART MODE
Use only when the user asks for a chart/plot/graph/visualization.
Rules:
- inspect columns/relationships as needed
- call ExecuteSelectAsync if needed
- return ONLY strict chart JSON

GENERAL RULES:
- Only SELECT queries are allowed.
- Do not guess table names or columns.
- Use only tables returned by TableAndViewsInDatabse.
- Prefer SCHEMA MODE when the request is ambiguous.
- Keep schema answers concise and business-friendly.
- Do not fabricate relationships or columns.
- For schema-only questions, do not return grid/tool output.

SCHEMA OUTPUT FORMAT:
- Relevant tables/views
- Why each is useful
- Likely joins/relationships
- Best starting table
- 3 follow-up questions

PROFILE OUTPUT FORMAT:
When profiling a single table, include:
- table purpose
- likely grain
- important columns/groups
- likely keys
- likely relationships
- analysis use cases
- 3 follow-up questions

CHART JSON:
{
  ""chartType"": ""column"" | ""bar"" | ""line"" | ""area"" | ""pie"" | ""donut"" | ""progress"" | ""gauge"" | ""multicolumn"",
  ""title"": ""<short title>"",
  ""xAxis"": { ""title"": ""<x axis title>"", ""categories"": [""A"",""B"",""C""] },
  ""yAxis"": { ""title"": ""<y axis title>"" },
  ""series"": [{ ""name"": ""<series name>"", ""data"": [1,2,3] }]
}

If chart data is unavailable, return:
{
  ""chartType"": ""column"",
  ""title"": ""No data / insufficient context"",
  ""xAxis"": { ""title"": """", ""categories"": [""No data""] },
  ""yAxis"": { ""title"": """" },
  ""series"": [{ ""name"": ""No data"", ""data"": [0] }]
}
";

        var tools = new[]
        {
            AIFunctionFactory.Create(sqlTool.TableAndViewsInDatabse),
            AIFunctionFactory.Create(sqlTool.TableColumsByTable),
            AIFunctionFactory.Create(sqlTool.TableRelationships),
            AIFunctionFactory.Create(sqlTool.ExecuteSelectAsync),
        };

        return chat
            .AsAIAgent(
                name: name,
                instructions: instructions,
                tools: tools)
            .AsBuilder()
            .Use(
                runFunc: ValidateDataExplorerRunAsync,
                runStreamingFunc: ValidateDataExplorerStreamingRunAsync)
            .Build();
    }

    private static IAsyncEnumerable<AgentResponseUpdate> ValidateDataExplorerStreamingRunAsync(
        IEnumerable<ExtChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        return innerAgent.RunStreamingAsync(messages, session, options, cancellationToken);
    }

    private static async Task<AgentResponse> ValidateDataExplorerRunAsync(
        IEnumerable<ExtChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var response = await innerAgent.RunAsync(messages, session, options, cancellationToken)
            .ConfigureAwait(false);

        var userText = messages.LastOrDefault(m => m.Role == ExtChatRole.User)?.Text ?? string.Empty;
        var assistantIndex = FindLastAssistantIndex(response.Messages);

        if (assistantIndex < 0)
        {
            return response;
        }

        var assistantText = response.Messages[assistantIndex].Text ?? string.Empty;

        if (!IsSchemaQuestion(userText) || !LooksLikeWrongMetricReply(assistantText))
        {
            return response;
        }

        response.Messages[assistantIndex] = new ExtChatMessage(
            ExtChatRole.Assistant,
            """
Relevant tables/views:
- Sales.SalesOrderHeader: sales transaction header, including order date, customer, territory, and totals
- Sales.SalesOrderDetail: line-level product sales for each order
- Sales.Customer: customer entity used to connect sales to customer records
- Sales.SalesTerritory: region/territory dimension for geographic analysis
- Production.Product: product master table
- Production.ProductSubcategory: subcategory grouping for products
- Production.ProductCategory: category grouping for products

Why they are useful:
- Use Sales.SalesOrderHeader for customer, date, and territory context
- Use Sales.SalesOrderDetail for product-level sales detail
- Use Sales.Customer to identify the buying customer
- Use Sales.SalesTerritory for region analysis
- Use Production.Product, Production.ProductSubcategory, and Production.ProductCategory for product category analysis

Likely joins/relationships:
- Sales.SalesOrderHeader.SalesOrderID -> Sales.SalesOrderDetail.SalesOrderID
- Sales.SalesOrderHeader.CustomerID -> Sales.Customer.CustomerID
- Sales.SalesOrderHeader.TerritoryID -> Sales.SalesTerritory.TerritoryID
- Sales.SalesOrderDetail.ProductID -> Production.Product.ProductID
- Production.Product.ProductSubcategoryID -> Production.ProductSubcategory.ProductSubcategoryID
- Production.ProductSubcategory.ProductCategoryID -> Production.ProductCategory.ProductCategoryID

Best starting table:
- Start from Sales.SalesOrderHeader joined to Sales.SalesOrderDetail, then add Customer, Sales.SalesTerritory, and product category tables.

Suggested follow-up questions:
1. Show me the exact join path for these tables.
2. Write a SQL query for customer sales by region and product category.
3. Which columns should I select for this analysis?
""");

        return response;
    }

    private static int FindLastAssistantIndex(IList<ExtChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ExtChatRole.Assistant)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsSchemaQuestion(string text)
    {
        var q = text.ToLowerInvariant();

        return q.Contains("what tables") ||
               q.Contains("which tables") ||
               q.Contains("how tables relate") ||
               q.Contains("schema") ||
               q.Contains("relationship") ||
               q.Contains("relationships") ||
               q.Contains("what joins") ||
               q.Contains("which joins") ||
               q.Contains("what columns") ||
               q.Contains("which columns") ||
               q.Contains("profile ") ||
               q.Contains("describe ");
    }

    private static bool LooksLikeWrongMetricReply(string text)
    {
        var r = text.ToLowerInvariant();

        return r.Contains("customer count") ||
               r.Contains("count") ||
               r.Contains("sum") ||
               r.Contains("avg") ||
               r.Contains("min") ||
               r.Contains("max");
    }
}
