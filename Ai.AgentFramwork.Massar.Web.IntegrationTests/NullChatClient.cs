//using System.ClientModel;
//using Microsoft.Extensions.AI;
//using OpenAI.Chat;

//namespace Ai.AgentFramwork.Massar.Web.Tests;

//internal sealed class NullChatClient : IChatClient
//{
//    public static readonly NullChatClient Instance = new();

//    public object? GetService(Type serviceType, object? serviceKey = null) => null;

//    public void Dispose()
//    {
//    }

//    public Task<ChatResponse> GetResponseAsync(
//        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
//        ChatOptions? options = null,
//        CancellationToken cancellationToken = default)
//    {
//        throw new NotSupportedException("NullChatClient should not be used when hard-guards succeed.");
//    }

//    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
//        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
//        ChatOptions? options = null,
//        CancellationToken cancellationToken = default)
//    {
//        throw new NotSupportedException("NullChatClient should not be used when hard-guards succeed.");
//    }
//}