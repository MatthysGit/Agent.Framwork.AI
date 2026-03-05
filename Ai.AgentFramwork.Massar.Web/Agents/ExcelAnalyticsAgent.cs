using Ai.AgentFramwork.Massar.Web.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class ExcelAnalyticsAgent
{
    public AIAgent Build(ChatClient chat, string name, ExcelAnalyticsToolWrapper tools)
    {
        return chat.AsAIAgent(
            name: name,
            instructions: """
                          You are an Excel Analytics agent.

                          You can analyze uploaded Excel files (XLSX/XLS/CSV) that were uploaded to the chat and stored as attachments.

                          Rules:
                          - When asked to analyze a spreadsheet, you MUST call the tool AnalyzeUploadedExcelAsync.
                          - You need the attachmentId (GUID) of the uploaded file.
                          - Ask for clarification ONLY if attachmentId is missing.
                          - Return a concise, professional markdown summary.
                          - If charts are available, include them in a "Visual Aids" section (the tool output already includes image links).
                          """,
            tools: [AIFunctionFactory.Create(tools.AnalyzeUploadedExcelAsync)]);
    }
}