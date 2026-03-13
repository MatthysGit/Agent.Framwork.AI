using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Models;
using Ai.AgentFramwork.Massar.Web.Services;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using Ai.AgentFramwork.Massar.Web.Services.Chat.charts;
using Ai.AgentFramwork.Massar.Web.Services.Documents;
using Ai.AgentFramwork.Massar.Web.Services.DocumentSeach;
using Ai.AgentFramwork.Massar.Web.Tools;
using DocumentFormat.OpenXml.Math;
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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Ai.AgentFramwork.Massar.Web.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ChatServiceSendAsyncIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public ChatServiceSendAsyncIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }
    //[Fact]
    //public void BuildConfiguration_Should_load_required_settings()
    //{
    //    var config = BuildConfiguration();

    //    config["OpenAI:Key"].Should().NotBeNullOrWhiteSpace();
    //    config.GetConnectionString("AppDb").Should().NotBeNullOrWhiteSpace();
    //    config.GetConnectionString("AI_DB").Should().NotBeNullOrWhiteSpace();
    //}

    //-------------------------------------------------------------------------------------------------
    //AnomalyDetectionAgent
    //-------------------------------------------------------------------------------------------------

    #region AnomalyDetectionAgent

    [Fact]
    public async Task AD_01()
    {
        var (sut, tracker) = CreateSut();

        var userMessage = new ChatMessage(
            ChatRole.User,
            "Find anomalous daily sales spikes for the last 12 months by territory.");

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

        foreach (var step in tracker.Steps)
            route += step + " -> ";

        tracker.Steps.Should().NotBeEmpty();
        route.Should().ContainAll("RouterAgent", "AnomalyDetectionAgent", "SqlAgent");
    }

    //[Fact]
    //public async Task AD_02()
    //{
    //    var (sut, tracker) = CreateSut();

    //    var userMessage = new ChatMessage(
    //        ChatRole.User,
    //        "Identify products with unusually high return rates this quarter.");

    //    IEnumerable<ChatMessage>? finalBatch = null;

    //    await foreach (var batch in sut.SendAsyc(userMessage))
    //    {
    //        finalBatch = batch;
    //    }

    //    finalBatch.Should().NotBeNull();

    //    var messages = finalBatch!.ToList();
    //    messages.Should().NotBeEmpty();
    //    messages.Should().Contain(m => m.Role == ChatRole.User);
    //    messages.Should().Contain(m => m.Role == ChatRole.Assistant);

    //    var assistant = messages.Last(m => m.Role == ChatRole.Assistant);
    //    var text = string.Concat(assistant.Contents.OfType<TextContent>().Select(t => t.Text));

    //    text.Should().NotBeNullOrWhiteSpace();
    //    text.Should().NotContainEquivalentOf("unauthorized access");
    //    text.Should().NotContainEquivalentOf("not valid json");
    //    text.Should().NotContainEquivalentOf("blocked keyword");


    //    string route = "";

    //    foreach (var step in tracker.Steps)
    //        route += step + " -> ";

    //    tracker.Steps.Should().NotBeEmpty();
    //    route.Should().ContainAll("RouterAgent", "AnomalyDetectionAgent", "SqlAgent");
    //}

    //[Fact]
    //public async Task AD_03()
    //{
    //    var (sut, tracker) = CreateSut();

    //    var userMessage = new ChatMessage(
    //        ChatRole.User,
    //        "Show vendors with abnormal increase in average purchase cost over the past 6 months.");

    //    IEnumerable<ChatMessage>? finalBatch = null;

    //    await foreach (var batch in sut.SendAsyc(userMessage))
    //    {
    //        finalBatch = batch;
    //    }

    //    finalBatch.Should().NotBeNull();

    //    var messages = finalBatch!.ToList();
    //    messages.Should().NotBeEmpty();
    //    messages.Should().Contain(m => m.Role == ChatRole.User);
    //    messages.Should().Contain(m => m.Role == ChatRole.Assistant);

    //    var assistant = messages.Last(m => m.Role == ChatRole.Assistant);
    //    var text = string.Concat(assistant.Contents.OfType<TextContent>().Select(t => t.Text));

    //    text.Should().NotBeNullOrWhiteSpace();
    //    text.Should().NotContainEquivalentOf("unauthorized access");
    //    text.Should().NotContainEquivalentOf("not valid json");
    //    text.Should().NotContainEquivalentOf("blocked keyword");


    //    string route = "";

    //    foreach (var step in tracker.Steps)
    //        route += step + " -> ";

    //    tracker.Steps.Should().NotBeEmpty();
    //    route.Should().ContainAll("RouterAgent", "AnomalyDetectionAgent", "SqlAgent");


    //}

    //[Fact]
    //public async Task AD_04()
    //{
    //    var (sut, tracker) = CreateSut();

    //    var userMessage = new ChatMessage(
    //        ChatRole.User,
    //        "Find anomalies in a newly introduced product with only 3 days of sales.");

    //    IEnumerable<ChatMessage>? finalBatch = null;

    //    await foreach (var batch in sut.SendAsyc(userMessage))
    //    {
    //        finalBatch = batch;
    //    }

    //    finalBatch.Should().NotBeNull();

    //    var messages = finalBatch!.ToList();
    //    messages.Should().NotBeEmpty();
    //    messages.Should().Contain(m => m.Role == ChatRole.User);
    //    messages.Should().Contain(m => m.Role == ChatRole.Assistant);

    //    var assistant = messages.Last(m => m.Role == ChatRole.Assistant);
    //    var text = string.Concat(assistant.Contents.OfType<TextContent>().Select(t => t.Text));

    //    text.Should().NotBeNullOrWhiteSpace();
    //    text.Should().NotContainEquivalentOf("unauthorized access");
    //    text.Should().NotContainEquivalentOf("not valid json");
    //    text.Should().NotContainEquivalentOf("blocked keyword");


    //    string route = "";

    //    foreach (var step in tracker.Steps)
    //        route += step + " -> ";

    //    tracker.Steps.Should().NotBeEmpty();
    //    route.Should().ContainAll("RouterAgent", "AnomalyDetectionAgent", "SqlAgent");


    //}

    #endregion

    //-------------------------------------------------------------------------------------------------
    //DataExplorerAgent
    //-------------------------------------------------------------------------------------------------

    #region DataExplorerAgent 

    [Fact]
    public async Task DE_01()
        => await AssertRouteAsync(
            "What tables should I use to analyze customer sales by region and product category?",
            ChatAgentFactory.DataExplorerAgentName);

    //[Fact]
    //public async Task DE_02()
    //    => await AssertRouteAsync(
    //        "Profile Sales.SalesOrderHeader.",
    //        ChatAgentFactory.DataExplorerAgentName);

    //[Fact]
    //public async Task DE_03()
    //    => await AssertRouteAsync(
    //        "Explain how employees relate to sales performance.",
    //        ChatAgentFactory.DataExplorerAgentName);

    #endregion


    //-------------------------------------------------------------------------------------------------
    //DataIntelligenceAgent
    //-------------------------------------------------------------------------------------------------

    #region DataIntelligenceAgent

    [Fact]
    public async Task DI_01()
        => await AssertRouteAsync(
            "Summarize the most important sales insights for the last quarter.",
            ChatAgentFactory.DataIntelligenceAgentName);


    //[Fact]
    //public async Task DI_02()
    //    => await AssertRouteAsync(
    //        "Why did accessories revenue decline last month?",
    //        ChatAgentFactory.DataIntelligenceAgentName);


    //[Fact]
    //public async Task DI_03()
    //    => await AssertRouteAsync(
    //        "Identify products where sales are strong but procurement or stock patterns may be risky.",
    //        ChatAgentFactory.DataIntelligenceAgentName);


    //[Fact]
    //public async Task DI_04()
    //    => await AssertRouteAsync(
    //        "Tell me the exact reason profit dropped for one SKU with sparse data.",
    //        ChatAgentFactory.DataIntelligenceAgentName);

    #endregion

    //-------------------------------------------------------------------------------------------------
    // Added routing coverage tests
    //-------------------------------------------------------------------------------------------------

    #region AddedRoutingCoverage

    [Fact]
    public async Task DS_001()
        => await AssertRouteAsync(
            "Segment customers into high, medium, and low value based on revenue and purchase frequency.",
            ChatAgentFactory.DataSegmentationAgentName,
            ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task DS_002()
    //    => await AssertRouteAsync(
    //        "Segment products by demand, revenue, and margin.",
    //        ChatAgentFactory.DataSegmentationAgentName,
    //        ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task DS_003()
    //    => await AssertRouteAsync(
    //        "Segment sales territories by growth rate and average order value.",
    //        ChatAgentFactory.DataSegmentationAgentName,
    //        ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task DS_004()
    //    => await AssertRouteAsync(
    //        "Why is customer X in the high-value segment?",
    //        ChatAgentFactory.DataSegmentationAgentName,
    //        ChatAgentFactory.SqlAgentName);


    #endregion


    #region DocumentSearchAgent

    [Fact]
    public async Task DOC_001()
        => await AssertRouteAsync(
            "Find documentation about sales order tables in AdventureWorks2025.",
            ChatAgentFactory.DocumentSearchAgentName);

    //[Fact]
    //public async Task DOC_002()
    //    => await AssertRouteAsync(
    //        "What is the official definition of gross margin in our project documents?",
    //        ChatAgentFactory.DocumentSearchAgentName);

    //[Fact]
    //public async Task DOC_003()
    //    => await AssertRouteAsync(
    //        "Find docs related to customer churn or retention.",
    //        ChatAgentFactory.DocumentSearchAgentName);

    //[Fact]
    //public async Task DOC_004()
    //    => await AssertRouteAsync(
    //        "Find documents about cryptocurrency payments in AdventureWorks2025.",
    //        ChatAgentFactory.DocumentSearchAgentName);

    #endregion


    #region ExecutiveInsightAgent

    [Fact]
    public async Task EI_001()
        => await AssertRouteAsync(
            "Prepare an executive summary of this month’s performance.",
            ChatAgentFactory.ExecutiveInsightAgentName);

    //[Fact]
    //public async Task EI_002()
    //    => await AssertRouteAsync(
    //        "Summarize sales, profitability, and customer health for leadership review.",
    //        ChatAgentFactory.ExecutiveInsightAgentName);

    //[Fact]
    //public async Task EI_003()
    //    => await AssertRouteAsync(
    //        "What should the COO worry about this quarter?",
    //        ChatAgentFactory.ExecutiveInsightAgentName);

    //[Fact]
    //public async Task EI_004()
    //    => await AssertRouteAsync(
    //        "Explain last quarter’s performance for a CFO audience.",
    //        ChatAgentFactory.ExecutiveInsightAgentName);


    #endregion


    #region ForecastingAgent

    [Fact]
    public async Task FC_001()
        => await AssertRouteAsync(
            "Forecast monthly sales for the next 6 months.",
            ChatAgentFactory.ForecastingAgentName);

    //[Fact]
    //public async Task FC_002()
    //    => await AssertRouteAsync(
    //        "Forecast next quarter sales by product category.",
    //        ChatAgentFactory.ForecastingAgentName,
    //        ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task FC_003()
    //    => await AssertRouteAsync(
    //        "Forecast holiday-season bike sales based on prior years.",
    //        ChatAgentFactory.ForecastingAgentName,
    //        ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task FC_004()
    //    => await AssertRouteAsync(
    //        "Forecast weekly demand for a product launched last week.",
    //        ChatAgentFactory.ForecastingAgentName,
    //        ChatAgentFactory.SqlAgentName);


    //[Fact]
    //public async Task FC_005()
    //    => await AssertRouteAsync(
    //        "Using historical data, forecast total monthly sales revenue for the next 6 months. Base the forecast on the monthly sales trend from the past 3 years.",
    //        ChatAgentFactory.ForecastingAgentName,
    //        ChatAgentFactory.SqlAgentName);



    #endregion


    #region LlmChatAgent

    [Fact]
    public async Task LC_001()
        => await AssertRouteAsync(
            "Why is the sky blue?",
            ChatAgentFactory.LlmChatAgentName);

    //[Fact]
    //public async Task LC_002()
    //{
    //    var (sut, tracker) = CreateSut();

    //    await RunConversationAsync(sut, "Show top 10 customers by revenue.");
    //    await RunConversationAsync(sut, "Which of them are in Europe?");

    //    tracker.Steps.Should().Contain(s =>
    //        s.Contains("RouterAgent", StringComparison.OrdinalIgnoreCase) &&
    //        s.Contains(ChatAgentFactory.SqlAgentName, StringComparison.OrdinalIgnoreCase));

    //    tracker.Steps.Should().Contain(s =>
    //        s.Contains("RouterAgent", StringComparison.OrdinalIgnoreCase) &&
    //        s.Contains(ChatAgentFactory.LlmChatAgentName, StringComparison.OrdinalIgnoreCase));
    //}

    //[Fact]
    //public async Task LC_003()
    //    => await AssertRouteAsync(
    //        "Show profit by region.",
    //        ChatAgentFactory.LlmChatAgentName);

    //[Fact]
    //public async Task LC_004()
    //    => await AssertRouteAsync(
    //        "Tell me about subscription revenue.",
    //        ChatAgentFactory.LlmChatAgentName);

    #endregion


    [Fact]
    public async Task SQL_001()
        => await AssertRouteAsync(
            "Write SQL to get total sales by year.",
            ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task SQL_002()
    //    => await AssertRouteAsync(
    //        "Get top 10 customers by revenue with territory name.",
    //        ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task SQL_003()
    //    => await AssertRouteAsync(
    //        "Delete customers with no orders.",
    //        ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task SQL_004()
    //    => await AssertRouteAsync(
    //        "Explain the SQL you generated for sales by product category.",
    //        ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task SQL_005()
    //    => await AssertRouteAsync(
    //        "Query sales from table Sales.MonthlyRevenueSummary.",
    //        ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task SQL_006()
    //    => await AssertRouteAsync(
    //        "Show 10 representative rows from Production.Product with important columns.",
    //        ChatAgentFactory.SqlAgentName);



    [Fact]
    public async Task WI_001()
        => await AssertRouteAsync(
            "Simulate a 5% price increase on bikes and estimate revenue impact.",
            ChatAgentFactory.WhatIfSimulationAgentName);

    //[Fact]
    //public async Task WI_002()
    //    => await AssertRouteAsync(
    //        "What happens to margin if average discount is reduced by 2 percentage points?",
    //        ChatAgentFactory.WhatIfSimulationAgentName,
    //        ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task WI_003()
    //    => await AssertRouteAsync(
    //        "What if European order volume grows by 10% next quarter?",
    //        ChatAgentFactory.WhatIfSimulationAgentName,
    //        ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task WI_004()
    //    => await AssertRouteAsync(
    //        "What if we double helmet prices and keep demand unchanged?",
    //        ChatAgentFactory.WhatIfSimulationAgentName,
    //        ChatAgentFactory.SqlAgentName);

    #region Cross-agent

    [Fact]
    public async Task INT_001()
        => await AssertRouteAsync(
            "What are the top 10 customers by revenue with territory name?",
            ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task INT_002()
    //{
    //    var (sut, tracker) = CreateSut();

    //    await RunConversationAsync(sut, "Which tables hold monthly sales?");
    //    await RunConversationAsync(sut, "Forecast monthly sales for the next 6 months.");

    //    tracker.Steps.Should().Contain(s =>
    //        s.Contains("RouterAgent", StringComparison.OrdinalIgnoreCase) &&
    //        s.Contains(ChatAgentFactory.DataExplorerAgentName, StringComparison.OrdinalIgnoreCase));

    //    tracker.Steps.Should().Contain(s =>
    //        s.Contains("RouterAgent", StringComparison.OrdinalIgnoreCase) &&
    //        s.Contains(ChatAgentFactory.ForecastingAgentName, StringComparison.OrdinalIgnoreCase));
    //}

    //[Fact]
    //public async Task INT_003()
    //{
    //    var (sut, tracker) = CreateSut();

    //    await RunConversationAsync(sut, "What is the official definition of gross margin in our project documents?");
    //    await RunConversationAsync(sut, "Write SQL to calculate gross margin by product category.");

    //    tracker.Steps.Should().Contain(s =>
    //        s.Contains("RouterAgent", StringComparison.OrdinalIgnoreCase) &&
    //        s.Contains(ChatAgentFactory.DocumentSearchAgentName, StringComparison.OrdinalIgnoreCase));

    //    tracker.Steps.Should().Contain(s =>
    //        s.Contains("RouterAgent", StringComparison.OrdinalIgnoreCase) &&
    //        s.Contains(ChatAgentFactory.SqlAgentName, StringComparison.OrdinalIgnoreCase));
    //}

    //[Fact]
    //public async Task INT_004()
    //{
    //    var (sut, tracker) = CreateSut();

    //    await RunConversationAsync(sut, "Find anomalous daily sales spikes for the last 12 months by territory.");
    //    await RunConversationAsync(sut, "Prepare an executive summary of the business risks from those anomalies.");

    //    tracker.Steps.Should().Contain(s =>
    //        s.Contains("RouterAgent", StringComparison.OrdinalIgnoreCase) &&
    //        s.Contains(ChatAgentFactory.AnomalyDetectionAgentName, StringComparison.OrdinalIgnoreCase));

    //    tracker.Steps.Should().Contain(s =>
    //        s.Contains("RouterAgent", StringComparison.OrdinalIgnoreCase) &&
    //        s.Contains(ChatAgentFactory.ExecutiveInsightAgentName, StringComparison.OrdinalIgnoreCase));
    //}

    #endregion



    #region Non-functional

    [Fact]
    public async Task NF_001()
    {
        var started = DateTime.UtcNow;
        await AssertRouteAsync(
            "Explore reseller sales performance by country, region, and product category. Show the top patterns, strongest markets, weakest markets, and any notable trends.",
            ChatAgentFactory.SqlAgentName);

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromMinutes(2));
    }

    //[Fact]
    //public async Task NF_002()
    //    => await AssertRouteAsync(
    //        "Analyze employee compensation by department and individual.",
    //        ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task NF_003()
    //    => await AssertRouteAsync(
    //        "Explain the SQL you generated for sales by product category.",
    //        ChatAgentFactory.SqlAgentName);

    //[Fact]
    //public async Task NF_004()
    //{
    //    var first = await RunConversationWithTrackerAsync("Write SQL to get total sales by year.");
    //    var second = await RunConversationWithTrackerAsync("Write SQL to get total sales by year.");

    //    first.Should().Contain(ChatAgentFactory.SqlAgentName);
    //    second.Should().Contain(ChatAgentFactory.SqlAgentName);
    //}

    //[Fact]
    //public async Task NF_005()
    //{
    //    var response = await RunConversationAsync("Tell me about the nonexistent KPI HyperMarginX from table Sales.FakeRevenue.");
    //    response.Should().NotContainEquivalentOf("Sales.FakeRevenue has");
    //    response.Should().NotContainEquivalentOf("HyperMarginX is");
    //}
    #endregion

    private async Task AssertRouteAsync(string input, string expectedPrimaryAgent, params string[] expectedAdditionalAgents)
    {
        var sw = Stopwatch.StartNew();
        _output.WriteLine($"[{DateTimeOffset.Now:O}] AssertRouteAsync started");
        _output.WriteLine($"Input: {input}");

        var (sut, tracker) = CreateSut();

        var response = await RunConversationAsync(sut, input);

        response.Should().NotBeNullOrWhiteSpace();

        var route = string.Join(" ", tracker.Steps);

        tracker.Steps.Should().NotBeEmpty();
        route.Should().ContainAll("RouterAgent", expectedPrimaryAgent);

        if (expectedAdditionalAgents is { Length: > 0 })
        {
            route.Should().ContainAll(expectedAdditionalAgents);
        }

        _output.WriteLine($"Route: {route}");
        _output.WriteLine($"AssertRouteAsync completed in {sw.Elapsed.TotalSeconds:N2}s");
    }

    private async Task<string> RunConversationAsync(string input)
    {
        var (sut, _) = CreateSut();
        return await RunConversationAsync(sut, input);
    }

    private async Task<string> RunConversationAsync(ChatService sut, string input, [CallerMemberName] string caller = "")
    {
        var total = Stopwatch.StartNew();
        _output.WriteLine($"[{DateTimeOffset.Now:O}] {caller}: SendAsyc starting");

        var userMessage = new ChatMessage(ChatRole.User, input);
        IEnumerable<ChatMessage>? finalBatch = null;
        var batchCount = 0;

        var stream = Stopwatch.StartNew();
        await foreach (var batch in sut.SendAsyc(userMessage))
        {
            batchCount++;
            finalBatch = batch;
            _output.WriteLine($"[{DateTimeOffset.Now:O}] {caller}: received batch {batchCount} at {stream.Elapsed.TotalSeconds:N2}s");
        }

        finalBatch.Should().NotBeNull();
        var messages = finalBatch!.ToList();
        messages.Should().Contain(m => m.Role == ChatRole.Assistant);

        var assistant = messages.Last(m => m.Role == ChatRole.Assistant);
        var text = string.Concat(assistant.Contents.OfType<TextContent>().Select(t => t.Text));

        text.Should().NotContainEquivalentOf("unauthorized access");
        text.Should().NotContainEquivalentOf("not valid json");
        text.Should().NotContainEquivalentOf("blocked keyword");

        _output.WriteLine($"[{DateTimeOffset.Now:O}] {caller}: SendAsyc completed in {total.Elapsed.TotalSeconds:N2}s; batches={batchCount}; messageCount={messages.Count}; assistantChars={text.Length}");

        return text;
    }

    private async Task<string> RunConversationWithTrackerAsync(string input)
    {
        var (sut, tracker) = CreateSut();
        _ = await RunConversationAsync(sut, input);
        var route = string.Join(" -> ", tracker.Steps);
        _output.WriteLine($"Tracker route: {route}");
        return route;
    }

    #region Sut

    private (ChatService Sut, RouterDecisionTrackerService Tracker) CreateSut([CallerMemberName] string caller = "")
    {
        var total = Stopwatch.StartNew();

        var step = Stopwatch.StartNew();
        var config = BuildConfiguration();
        _output.WriteLine($"[{DateTimeOffset.Now:O}] {caller}: BuildConfiguration {step.Elapsed.TotalMilliseconds:N0} ms");

        step.Restart();
        var dbFactory = CreateDbFactory(config);
        _output.WriteLine($"[{DateTimeOffset.Now:O}] {caller}: CreateDbFactory {step.Elapsed.TotalMilliseconds:N0} ms");

        step.Restart();
        var auth = new TestAuthenticationStateProvider(roleId: 3, userId: "bce7002116d44128a567715b41dc5405");

        var session = new ChatSession();
        var repo = new ChatConversationRepository(dbFactory);

        var attachmentStore = new ChatAttachmentStore(
            new TestWebHostEnvironment(),
            dbFactory,
            new NoOpChatConversationAttachmentIngestService());
        _output.WriteLine($"[{DateTimeOffset.Now:O}] {caller}: Session/repo/attachmentStore {step.Elapsed.TotalMilliseconds:N0} ms");

        step.Restart();
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
        _output.WriteLine($"[{DateTimeOffset.Now:O}] {caller}: Agent factory graph {step.Elapsed.TotalMilliseconds:N0} ms");

        step.Restart();
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
        _output.WriteLine($"[{DateTimeOffset.Now:O}] {caller}: Tools/doc tools/tracker {step.Elapsed.TotalMilliseconds:N0} ms");

        step.Restart();
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

        _output.WriteLine($"[{DateTimeOffset.Now:O}] {caller}: ChatService ctor {step.Elapsed.TotalMilliseconds:N0} ms");
        _output.WriteLine($"[{DateTimeOffset.Now:O}] {caller}: CreateSut total {total.Elapsed.TotalMilliseconds:N0} ms");

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

    #endregion

}