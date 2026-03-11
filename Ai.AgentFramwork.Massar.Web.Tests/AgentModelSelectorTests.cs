using Ai.AgentFramwork.Massar.Web.Agents;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Services.Chat;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Ai.AgentFramwork.Massar.Web.Tests;

public sealed class AgentModelSelectorTests
{
    [Fact]
    public void GetModelForAgent_Should_return_default_when_database_has_no_active_mapping()
    {
        var factory = new TestDbContextFactory(CreateOptions());
        var sut = new AgentModelSelector(factory);

        var result = sut.GetModelForAgent("UnknownAgent");

        result.Should().Be("gpt-4.1-mini");
    }

    [Fact]
    public void GetModelForAgent_Should_return_active_model_from_database()
    {
        var options = CreateOptions();
        Seed(options, new Agent
        {
            AgentName = ChatAgentFactory.ForecastingAgentName,
            AgentModel = "gpt-5.4-thinking",
            IsActive = true,
            CreatedOn = DateTime.UtcNow
        });

        var sut = new AgentModelSelector(new TestDbContextFactory(options));

        var result = sut.GetModelForAgent(ChatAgentFactory.ForecastingAgentName);

        result.Should().Be("gpt-5.4-thinking");
    }

    [Fact]
    public void SetModelForAgent_Should_insert_new_mapping_when_agent_does_not_exist()
    {
        var options = CreateOptions();
        var factory = new TestDbContextFactory(options);
        var sut = new AgentModelSelector(factory);

        sut.SetModelForAgent(ChatAgentFactory.DataIntelligenceAgentName, "gpt-4.1");

        using var db = new AppDbContext(options);
        db.Agents.Should().ContainSingle(a =>
            a.AgentName == ChatAgentFactory.DataIntelligenceAgentName &&
            a.AgentModel == "gpt-4.1" &&
            a.IsActive);
    }

    [Fact]
    public void SetModelForAgent_Should_update_existing_mapping_when_agent_exists()
    {
        var options = CreateOptions();
        Seed(options, new Agent
        {
            AgentName = ChatAgentFactory.ExecutiveInsightAgentName,
            AgentModel = "gpt-4.1-mini",
            IsActive = true,
            CreatedOn = DateTime.UtcNow
        });

        var sut = new AgentModelSelector(new TestDbContextFactory(options));

        sut.SetModelForAgent(ChatAgentFactory.ExecutiveInsightAgentName, "gpt-5.4-thinking");

        using var db = new AppDbContext(options);
        db.Agents.Should().ContainSingle(a =>
            a.AgentName == ChatAgentFactory.ExecutiveInsightAgentName &&
            a.AgentModel == "gpt-5.4-thinking");
    }

    [Fact]
    public void GetModelForAgent_Should_fall_back_to_default_when_db_load_throws()
    {
        var sut = new AgentModelSelector(new ThrowingDbContextFactory());

        var result = sut.GetModelForAgent(ChatAgentFactory.SqlAgentName);

        result.Should().Be("gpt-4.1-mini");
    }

    private static DbContextOptions<AppDbContext> CreateOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    private static void Seed(DbContextOptions<AppDbContext> options, params Agent[] agents)
    {
        using var db = new AppDbContext(options);
        db.Agents.AddRange(agents);
        db.SaveChanges();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => throw new InvalidOperationException("boom");

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }
}
