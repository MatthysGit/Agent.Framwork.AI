using Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;
using System.Text.Json;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.Pipeline;

public sealed partial class ChatPipeline
{
    private static readonly JsonSerializerOptions _docSummaryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private async Task<PipelineResult> ExecuteDocumentSummaryAsync(
        string userText,
        string routerReason,
        CancellationToken ct)
    {
        var conversationId = _getConversationId();
        var searchJson = await _docSearchTool.SearchDocumentsAsync(conversationId, userText, ct: ct);

        DocumentSearchAgentResponse? searchPayload = null;
        try
        {
            searchPayload = JsonSerializer.Deserialize<DocumentSearchAgentResponse>(searchJson, _docSummaryJsonOptions);
        }
        catch
        {
            // fall through to a safe user-facing response below
        }

        if (searchPayload is null)
        {
            return new PipelineResult(
                searchJson ?? "No relevant documents found.",
                ChatAgentFactory.DocumentSummaryAgentName,
                routerReason);
        }

        if (string.IsNullOrWhiteSpace(searchPayload.Text))
        {
            var emptyJson = JsonSerializer.Serialize(new DocumentSearchAgentResponse(
                "No relevant document content was found to summarize.",
                searchPayload.Attachments ?? System.Array.Empty<DocumentAttachmentDescriptor>()));

            return new PipelineResult(emptyJson, ChatAgentFactory.DocumentSummaryAgentName, routerReason);
        }

        var summaryPrompt = $"""
Use the grounded document evidence below to produce a document summary.

User request:
{userText}

Grounded document evidence:
{searchPayload.Text}
""";

        await TrackRouteAsync(ChatAgentFactory.DocumentSummaryAgentName, "synthesis", ct);
        var summaryCall = await _caller.CallAgentAsync(
            ChatAgentFactory.DocumentSummaryAgentName,
            summaryPrompt,
            cancellationToken: ct);

        var summaryText = (summaryCall.Text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(summaryText))
        {
            summaryText =
                "Executive Summary\n" +
                "I could not produce a grounded summary from the retrieved document evidence.\n\n" +
                "Section Summaries\n" +
                "- Not clearly stated in the document.\n\n" +
                "Action Summary\n" +
                "- Not clearly stated in the document.\n\n" +
                "Risks / Decisions / Next Steps\n" +
                "Risks\n" +
                "- Not clearly stated in the document.\n\n" +
                "Decisions\n" +
                "- Not clearly stated in the document.\n\n" +
                "Next Steps\n" +
                "- Not clearly stated in the document.";
        }

        var checkedSummary = await RunPpiSafeAsync(userText, summaryText, ct);

        var resultJson = JsonSerializer.Serialize(new DocumentSearchAgentResponse(
            checkedSummary,
            searchPayload.Attachments ?? System.Array.Empty<DocumentAttachmentDescriptor>()));

        return new PipelineResult(
            resultJson,
            ChatAgentFactory.DocumentSummaryAgentName,
            routerReason + " (grounded via DocumentSearchTool)");
    }
}
