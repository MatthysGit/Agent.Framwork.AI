using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Tools;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Xunit;

namespace Ai.AgentFramwork.Massar.Web.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SqlAgentRealIntegrationTests
{
    private const string DefaultModel = "gpt-4.1";

    [Fact]
    public void BuildConfiguration_Should_load_required_settings_from_user_secrets_or_environment()
    {
        var config = BuildConfiguration();

        config["OpenAI:Key"].Should().NotBeNullOrWhiteSpace();
        config.GetConnectionString("AppDb").Should().NotBeNullOrWhiteSpace();
        config.GetConnectionString("AI_DB").Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SqlAgent_Should_execute_real_top_territories_request_end_to_end()
    {
        var sut = CreateSut();

        var result = await sut.CallAgentAsync(
            ChatAgentFactory.SqlAgentName,
            "Show me the top 10 sales territories by SalesYTD, including territory name, country region code, group, SalesYTD, and SalesLastYear.");

        result.Should().NotBeNull();
        result.AgentName.Should().Be(ChatAgentFactory.SqlAgentName);
        result.Text.Should().NotBeNullOrWhiteSpace();

        result.Text.Should().ContainEquivalentOf("territory");
        result.Text.Should().ContainEquivalentOf("sales");
        result.Text.Should().NotContainEquivalentOf("unauthorized access");
        result.Text.Should().NotContainEquivalentOf("not valid json");
        result.Text.Should().NotContainEquivalentOf("blocked keyword");
    }

    [Fact]
    public async Task SqlAgent_Performance_by_country_region_and_product_execution()
    {
        var sut = CreateSut();
        const string question = "Explore reseller sales performance by country, region, and product category. Show the top patterns, strongest markets, weakest markets, and any notable trends.";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await sut.CallAgentAsync(
            ChatAgentFactory.SqlAgentName,
            question,
            cts.Token);

        result.Should().NotBeNull();
        result.AgentName.Should().Be(ChatAgentFactory.SqlAgentName);
        result.Text.Should().NotBeNullOrWhiteSpace();
        result.Text.Should().NotContainEquivalentOf("incorrect syntax");
        result.Text.Should().NotContainEquivalentOf("exception");
        result.Text.Should().NotContainEquivalentOf("blocked keyword");
        result.Text.Should().NotContainEquivalentOf("unauthorized access");

        ShouldBeRelevantToPrompt(question, result.Text!);
    }

    [Fact]
    public async Task SqlAgent_Should_return_ranked_sales_territory_results()
    {
        var sut = CreateSut();

        var result = await sut.CallAgentAsync(
            ChatAgentFactory.SqlAgentName,
            "List the top 5 sales territories ordered by SalesYTD descending.");

        result.Should().NotBeNull();
        result.AgentName.Should().Be(ChatAgentFactory.SqlAgentName);
        result.Text.Should().NotBeNullOrWhiteSpace();
        result.Text.Should().ContainEquivalentOf("territor");
        result.Text.Should().ContainEquivalentOf("sales");
        result.Text.Should().NotContainEquivalentOf("unauthorized access");
        result.Text.Should().NotContainEquivalentOf("not valid json");
        result.Text.Should().NotContainEquivalentOf("blocked keyword");
    }

    [Fact]
    public async Task SqlAgent_Should_return_reseller_sales_performance_by_country_results()
    {
        var sut = CreateSut();

        var result = await sut.CallAgentAsync(
            ChatAgentFactory.SqlAgentName,
            "Explore reseller sales performance by country, region, and product category. Show the top patterns, strongest markets, weakest markets, and any notable trends.");

        result.Should().NotBeNull();
        result.AgentName.Should().Be(ChatAgentFactory.SqlAgentName);
        result.Text.Should().NotBeNullOrWhiteSpace();
        result.Text.Should().ContainEquivalentOf("sales");
        result.Text.Should().NotContainEquivalentOf("unauthorized access");
        result.Text.Should().NotContainEquivalentOf("not valid json");
        result.Text.Should().NotContainEquivalentOf("blocked keyword");
    }

    private static void ShouldBeRelevantToPrompt(string prompt, string answer)
    {
        answer.Should().NotBeNullOrWhiteSpace();

        var answerLower = answer.ToLowerInvariant();

        var keywords = prompt
            .Split(new[] { ' ', ',', '.', ':', ';', '?', '!' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 5)
            .Select(w => w.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

        var matches = keywords.Count(answerLower.Contains);

        matches.Should().BeGreaterThan(2);
    }

    private static AgentCallerTool CreateSut()
    {
        var config = BuildConfiguration();

        var auth = new TestAuthenticationStateProvider(roleId: 3);
        var sqlTool = new SqlServerSelectTool(config, auth);
        var chatClientFactory = new OpenAIChatClientFactory(config);

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(config)
            .AddSingleton<AuthenticationStateProvider>(auth)
            .BuildServiceProvider();

        var registry = new AgentRegistry(services, chatClientFactory);

        registry.Register(
            ChatAgentFactory.SqlAgentName,
            (sp, chatClient) => new SqlAgent().Build(chatClient, ChatAgentFactory.SqlAgentName, sqlTool));

        var modelSelector = new FixedModelSelector(DefaultModel);

        return new AgentCallerTool(
            registry,
            modelSelector,
            historyProvider: () => []);
    }

    private static IConfigurationRoot BuildConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<UserSecretsMarker>()
            .AddEnvironmentVariables()
            .Build();

        var openAiKey = configuration["OpenAI:Key"];
        var appDb = configuration.GetConnectionString("AppDb");
        var aiDb = configuration.GetConnectionString("AI_DB");

        if (string.IsNullOrWhiteSpace(openAiKey))
            throw new InvalidOperationException(
                "OpenAI:Key was not found in User Secrets or environment variables for the integration test project.");

        if (string.IsNullOrWhiteSpace(appDb))
            throw new InvalidOperationException(
                "ConnectionStrings:AppDb was not found in User Secrets or environment variables for the integration test project.");

        if (string.IsNullOrWhiteSpace(aiDb))
            throw new InvalidOperationException(
                "ConnectionStrings:AI_DB was not found in User Secrets or environment variables for the integration test project.");

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

        public TestAuthenticationStateProvider(int roleId)
        {
            var identity = new ClaimsIdentity(
            [
                new Claim("RoleId", roleId.ToString())
            ], "TestAuth");

            var principal = new ClaimsPrincipal(identity);
            _state = new AuthenticationState(principal);
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(_state);
    }

    private sealed class UserSecretsMarker
    {
    }
}