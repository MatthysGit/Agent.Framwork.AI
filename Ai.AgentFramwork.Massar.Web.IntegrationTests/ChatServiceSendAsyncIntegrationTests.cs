using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Models;
using Ai.AgentFramwork.Massar.Web.Services;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Services.Chat.charts;
using Ai.AgentFramwork.Massar.Web.Services.Documents;
using Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;
using Ai.AgentFramwork.Massar.Web.Tools;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using Xunit;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Ai.AgentFramwork.Massar.Web.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ChatServiceSendAsyncIntegrationTests
{
    [Fact]
    public void BuildConfiguration_Should_load_required_settings()
    {
        var config = BuildConfiguration();

        config["OpenAI:Key"].Should().NotBeNullOrWhiteSpace();
        config.GetConnectionString("AppDb").Should().NotBeNullOrWhiteSpace();
        config.GetConnectionString("AI_DB").Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SendAsyc_DataSegmentationAgent_Should_capture_route_flow()
    {
        var (sut, tracker) = CreateSut();

        var userMessage = new ChatMessage(
            ChatRole.User,
            "Segment customers based on total spend, order frequency, and recency. Identify the most valuable customer segments and describe each segment.");

        IEnumerable<ChatMessage>? finalBatch = null;

        await foreach (var batch in sut.SendAsyc(userMessage))
        {
            finalBatch = batch;
        }

        finalBatch.Should().NotBeNull();

        var messages = finalBatch!.ToList();
        messages.Should().NotBeEmpty();
        messages.Should().Contain(m => m.Role == ChatRole.User);
        messages.Should().Contain(m => m.Role == ChatRole.Assistant);

        var assistant = messages.Last(m => m.Role == ChatRole.Assistant);
        var text = string.Concat(assistant.Contents.OfType<TextContent>().Select(t => t.Text));

        text.Should().NotBeNullOrWhiteSpace();
        text.Should().NotContainEquivalentOf("unauthorized access");
        text.Should().NotContainEquivalentOf("not valid json");
        text.Should().NotContainEquivalentOf("blocked keyword");


        string route = "";

        Console.WriteLine("===== ROUTE FLOW =====");
        foreach (var step in tracker.Steps)
            route += step + " -> ";
        Console.WriteLine("======================");

        tracker.Steps.Should().NotBeEmpty();

        tracker.Steps.Should().Contain(s =>
            s.Contains("RouterAgent", StringComparison.OrdinalIgnoreCase) &&
            s.Contains("DataSegmentationAgent", StringComparison.OrdinalIgnoreCase));

        tracker.Steps.Should().Contain(s =>
            s.Contains("DataSegmentationAgent", StringComparison.OrdinalIgnoreCase));

        tracker.Steps.Should().Contain(s =>
            s.Contains("SqlAgent", StringComparison.OrdinalIgnoreCase));
    }

    private static (ChatService Sut, RouterDecisionTrackerService Tracker) CreateSut()
    {
        var config = BuildConfiguration();

        var dbFactory = CreateDbFactory(config);
        var auth = new TestAuthenticationStateProvider(roleId: 3, userId: "bce7002116d44128a567715b41dc5405");

        var session = new ChatSession();
        var repo = new ChatConversationRepository(dbFactory);

        var attachmentStore = new ChatAttachmentStore(
            new TestWebHostEnvironment(),
            dbFactory,
            new NoOpChatConversationAttachmentIngestService());

        var chatClientFactory = new OpenAIChatClientFactory(config);
        var modelSelector = new FixedModelSelector("gpt-4.1");

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(config)
            .AddSingleton<AuthenticationStateProvider>(auth)
            .BuildServiceProvider();

        var registry = new AgentRegistry(services, chatClientFactory);

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        var agentFactory = new ChatAgentFactory(
            config,
            registry,
            dbFactory,
            chatClientFactory,
            modelSelector,
            loggerFactory);

        var tools = new ChatTools(
            dbFactory,
            session,
            attachmentStore,
            new BraveSearchClient(config, new HttpClient()),
            new PieChartRenderer(),
            new BarChartRenderer(),
            new ColumnChartRenderer(),
            new MultiSeriesColumnChartRenderer(),
            new LineChartRenderer(),
            new MultiSeriesLineChartRenderer(),
            new AreaChartRenderer(),
            new MultiSeriesAreaChartRenderer(),
            new DonutChartRenderer(),
            new GaugeChartRenderer(),
            new ProgressBarsChartRenderer());

        var docSearchTool = new DocumentSearchTool(
            dbFactory,
            auth,
            new NoOpUnifiedDocumentSearchService(),
            session,
            NullAiChatClient.Instance);

        var docEditTool = new DocumentEditTool(
            dbFactory,
            auth,
            new NoOpUnifiedDocumentSearchService(),
            chatClientFactory,
            modelSelector);

        var tracker = new RouterDecisionTrackerService();

        var sut = new ChatService(
            auth,
            session,
            repo,
            attachmentStore,
            agentFactory,
            tools,
            dbFactory,
            docSearchTool,
            docEditTool,
            tracker);

        return (sut, tracker);
    }

    private static IDbContextFactory<AppDbContext> CreateDbFactory(IConfiguration config)
    {
        var connectionString = config.GetConnectionString("AI_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:AppDb is missing.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new PooledDbContextFactory<AppDbContext>(options);
    }

    private static IConfigurationRoot BuildConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<UserSecretsMarker>()
            .AddEnvironmentVariables()
            .Build();

        if (string.IsNullOrWhiteSpace(configuration["OpenAI:Key"]))
            throw new InvalidOperationException("OpenAI:Key was not found in User Secrets or environment variables.");

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("AppDb")))
            throw new InvalidOperationException("ConnectionStrings:AppDb was not found in User Secrets or environment variables.");

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("AI_DB")))
            throw new InvalidOperationException("ConnectionStrings:AI_DB was not found in User Secrets or environment variables.");

        return configuration;
    }

    private sealed class FixedModelSelector : IAgentModelSelector
    {
        private readonly string _modelKey;

        public FixedModelSelector(string modelKey)
        {
            _modelKey = modelKey;
        }

        public string GetModelForAgent(string agentName) => _modelKey;

        public void SetModelForAgent(string agentName, string modelKey)
        {
            throw new NotSupportedException("Test selector is fixed.");
        }
    }

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        private readonly AuthenticationState _state;

        public TestAuthenticationStateProvider(int roleId, string userId)
        {
            var identity = new ClaimsIdentity(
            [
                new Claim("RoleId", roleId.ToString()),
                new Claim(ClaimTypes.Role, roleId.ToString()),
                new Claim(AppClaimTypes.UserId, "bce7002116d44128a567715b41dc5405"),
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, "integration-test-user")
            ], "TestAuth");

            _state = new AuthenticationState(new ClaimsPrincipal(identity));
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(_state);
    }

    private sealed class NoOpChatConversationAttachmentIngestService : IChatConversationAttachmentIngestService
    {
        public Task IngestAsync(
            Guid conversationId,
            Guid attachmentId,
            string fileName,
            string contentType,
            byte[] bytes,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpUnifiedDocumentSearchService : IUnifiedDocumentSearchService
    {
        public Task<IReadOnlyList<DocumentSearchHit>> SearchAsync(
            string query,
            Guid conversationId,
            IReadOnlyCollection<int> roleIds,
            string embeddingModel,
            int topK = 8,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DocumentSearchHit>>(Array.Empty<DocumentSearchHit>());

        public Task<IReadOnlyList<DocumentSearchHit>> SearchScopedAsync(
            string query,
            Guid conversationId,
            string embeddingModel,
            int topK = 8,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DocumentSearchHit>>(Array.Empty<DocumentSearchHit>());
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Ai.AgentFramwork.Massar.Web.IntegrationTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class NullAiChatClient : IChatClient
    {
        public static readonly NullAiChatClient Instance = new();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("DocumentSearchTool LLM path should not be used in this test.");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("DocumentSearchTool LLM path should not be used in this test.");
        }
    }

    private sealed class UserSecretsMarker
    {
    }
}