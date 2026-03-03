using Microsoft.Extensions.AI;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

/// <summary>
/// Holds in-memory chat state for the current user/session:
/// transcript, streaming message, cancellation, active conversation id, and created assistant attachments.
/// </summary>
public sealed class ChatSession
{
    private readonly List<ChatMessage> _messages = new();

    private ChatMessage? _currentResponseMessage;
    private CancellationTokenSource? _currentResponseCancellation;

    private readonly List<ChatAttachmentInfo> _createdAssistantAttachments = new();

    public event Action? OnMessagesUpdated;

    private void Notify() => OnMessagesUpdated?.Invoke();

    public Guid? ActiveConversationId { get; private set; }

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public IReadOnlyList<ChatAttachmentInfo> CreatedAssistantAttachments => _createdAssistantAttachments;

    public ChatMessage? CurrentResponseMessage => _currentResponseMessage;

    public void SetActiveConversation(Guid? id)
    {
        ActiveConversationId = id;
        Notify();
    }

    public void ReplaceMessages(IEnumerable<ChatMessage> messages)
    {
        _messages.Clear();
        _messages.AddRange(messages);
        Notify();
    }

    public void AddMessage(ChatMessage message)
    {
        _messages.Add(message);
        Notify();
    }

    public CancellationToken BeginNewStreamingTurn()
    {
        CancelAnyCurrentResponse(addPartialToTranscript: true);

        _currentResponseCancellation = new CancellationTokenSource();
        // Optional: Notify(); // only if UI depends on "streaming state started"
        return _currentResponseCancellation.Token;
    }

    public void SetStreamingMessage(ChatMessage assistantInProgress)
    {
        _currentResponseMessage = assistantInProgress;
        Notify();
    }

    public void ClearStreamingMessage()
    {
        _currentResponseMessage = null;
        Notify();
    }

    public void TrackAssistantAttachment(ChatAttachmentInfo attachment)
    {
        _createdAssistantAttachments.Add(attachment);
        Notify(); // attachments affect UI rendering
    }

    public void ClearAssistantAttachments()
    {
        _createdAssistantAttachments.Clear();
        Notify();
    }

    public void StartNewChat()
    {
        CancelAnyCurrentResponse(addPartialToTranscript: false);

        ActiveConversationId = null;
        _messages.Clear();

        _currentResponseMessage = null;
        _createdAssistantAttachments.Clear();

        Notify();
    }

    public void CancelAnyCurrentResponse(bool addPartialToTranscript)
    {
        if (addPartialToTranscript && _currentResponseMessage is not null)
        {
            _messages.Add(_currentResponseMessage);
        }

        _currentResponseCancellation?.Cancel();
        _currentResponseCancellation = null;
        _currentResponseMessage = null;

        Notify();
    }
}