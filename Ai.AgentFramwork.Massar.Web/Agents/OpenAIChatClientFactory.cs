using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;

namespace Ai.AgentFramwork.Massar.Web.Agents;

#pragma warning disable OPENAI001
public sealed class OpenAIChatClientFactory : IChatClientFactory
{
    private readonly OpenAIClient _client;

    public OpenAIChatClientFactory(IConfiguration configuration)
    {
        var key = configuration["OpenAI:Key"] ?? "";
        _client = new OpenAIClient(key);
    }

    public ChatClient Create(string modelKey)
        => _client.GetChatClient(modelKey);
}