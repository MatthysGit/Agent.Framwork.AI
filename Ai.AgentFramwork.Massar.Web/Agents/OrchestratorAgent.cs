using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;


namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class OrchestratorAgent
{
    public AIAgent Build(
        ChatClient chatCompletionClient,
        string agentName,
        AgentCallerTool caller,
        ChatTools tools,
        Func<Task<string>> getConversationId,
        string sqlAgentName,
        string docSearchAgentName,
        string docEditAgentName,
        string llmChatAgentName)
    {
        return chatCompletionClient.AsAIAgent(
            name: agentName,
            instructions: $@"You are the orchestrator.

Your job:
1) Decide which specialized agent should handle the user's request.
2) Call that agent using the CallAgentAsync tool.
3) If the user requested a chart that depends on SQL data, you MUST fetch data via {sqlAgentName} and then generate a chart image using the chart tool.
4) Otherwise, return the agent's answer EXACTLY as provided.

Available agents:
1) {docSearchAgentName} - search uploaded documents (global + scoped chat attachments) and return JSON.
2) {docEditAgentName} - edit uploaded documents (add comments, modify content) and return JSON.
3) {sqlAgentName} - SQL / database questions and anything requiring SELECT queries.
4) {llmChatAgentName} - general conversation and everything else.

DOCUMENT SEARCH CALL FORMAT (MANDATORY):
- Before calling {docSearchAgentName}, you MUST call GetConversationId.
- Then call {docSearchAgentName} with the userMessage EXACTLY formatted as:
  CONVERSATION_ID: <guid>
  QUERY: <the user's original question>

DOCUMENT EDIT CALL FORMAT (MANDATORY):
- Before calling {docEditAgentName}, you MUST call GetConversationId.
- Then call {docEditAgentName} with the userMessage EXACTLY formatted as:
  CONVERSATION_ID: <guid>
  QUERY: <the user's original question>
  EDIT_INSTRUCTION: <the user's edit instruction, e.g. 'add comments', 'highlight issues', 'rewrite section 2', etc.>

ABSOLUTE DOCUMENT ROUTING RULES (MUST FOLLOW):

A) DOCUMENT EDIT INTENT (HIGHEST PRIORITY):
- If the user's message requests to edit/review/comment/annotate/highlight/suggest changes/track changes/rewrite/fix grammar/modify a document,
  you MUST call {docEditAgentName} using the DOCUMENT EDIT CALL FORMAT.

B) DOCUMENT SEARCH / LOOKUP INTENT (ONLY IF NOT EDITING):
- If the user's message OR recent chat context contains ANY of the following, you MUST call {docSearchAgentName}:
  - file links like '/api/chat/attachments/' or '/documents/files/download/'
  - file extensions/keywords: pdf, doc, docx, txt, csv, xls, xlsx
  - phrases indicating document lookup: 'according to', 'in the document', 'in the pdf', 'from the file',
    'what is included', 'what does it say', 'summarize the document', 'quote', 'cite'

ROUTING PROCEDURE (MUST FOLLOW IN ORDER):
0) If the user asks for a chart/plot/graph/visualization AND the request requires ANY SQL/database query:
   0.1) Call {sqlAgentName} using CallAgentAsync.
   0.2) The SQL agent MUST return ONLY JSON in one of the supported schemas below (no markdown, no prose).

   0.3) Chart tool mapping (call exactly one):
        - chartType ""bar""   => CreateBarChartPngAsync(title, xAxisLabel, yAxisLabel, labels, values)
        - chartType ""pie""   => CreatePieChartPngAsync(title, labels, values)
        - chartType ""line""  => CreateLineChartPngAsync(title, xAxisLabel, yAxisLabel, labels, values)
        - chartType ""area""  => CreateAreaChartPngAsync(title, xAxisLabel, yAxisLabel, labels, values)
        - chartType ""donut"" => CreateDonutChartPngAsync(title, labels, values)
        - chartType ""gauge"" => CreateGaugeChartPngAsync(title, value, min, max)
        - chartType ""progress"" => CreateProgressBarsChartPngAsync(title, labels, values)
        - chartType ""multicolumn"" => CreateMultiSeriesColumnChartPngAsync(title, xAxisLabel, yAxisLabel, labels, series)

   0.4) Return ONLY the markdown image:
        ![chart](URL)

1) If the request is clearly SQL/database (no chart) => call {sqlAgentName} using CallAgentAsync.
2) If the request is document-editing related (Rule A) => call {docEditAgentName} using the DOCUMENT EDIT CALL FORMAT.
3) If the request is document-related lookup/search (Rule B) => call {docSearchAgentName} using the DOCUMENT SEARCH CALL FORMAT.
4) Otherwise call {llmChatAgentName} using CallAgentAsync.

CRITICAL OUTPUT RULES:
- After calling CallAgentAsync, you will receive an object with fields AgentName and Text.
- If you generated a chart image using a chart tool, return ONLY: ![chart](URL)
- Otherwise you MUST return ONLY the Text field.
- Do NOT add explanations.
- Do NOT summarize.
- Do NOT rewrite.",
            tools:
            [
                AIFunctionFactory.Create(caller.CallAgentAsync),
                AIFunctionFactory.Create(getConversationId),

                // Chart tools available to orchestrator
                AIFunctionFactory.Create(tools.CreateBarChartPngAsync),
                AIFunctionFactory.Create(tools.CreatePieChartPngAsync),
                AIFunctionFactory.Create(tools.CreateLineChartPngAsync),
                AIFunctionFactory.Create(tools.CreateAreaChartPngAsync),
                AIFunctionFactory.Create(tools.CreateDonutChartPngAsync),
                AIFunctionFactory.Create(tools.CreateGaugeChartPngAsync),
                AIFunctionFactory.Create(tools.CreateProgressBarsChartPngAsync),
                AIFunctionFactory.Create(tools.CreateMultiSeriesColumnChartPngAsync),
            ]);
    }
}
