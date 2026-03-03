// File: Services/Email/DbGraphOptionsProvider.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Ai.AgentFramwork.Massar.Web.DBModels;

namespace Ai.AgentFramwork.Massar.Web.Services.Email;

public sealed class DbGraphOptionsProvider : IGraphOptionsProvider
{
    private const string CacheKey = "GraphOptions.Office365";
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IMemoryCache _cache;

    public DbGraphOptionsProvider(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache)
    {
        _dbFactory = dbFactory;
        _cache = cache;
    }

    public async Task<GraphOptions> GetAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out GraphOptions cached))
            return cached;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // ✅ Entity is Office365GraphConfig
        // ✅ Use a minimal query (no IsActive/UpdatedUtc assumptions)
        Office365GraphConfig? row = await db.Set<Office365GraphConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (row is null)
            throw new InvalidOperationException("No Office365GraphConfig row found in the database.");

        var opt = new GraphOptions
        {
            TenantId = row.TenantId ?? "",
            ClientId = row.ClientId ?? "",
            ClientSecret = row.ClientSecret ?? "",
            FromUser = row.FromUser ?? ""
        };

        _cache.Set(CacheKey, opt, TimeSpan.FromMinutes(5));
        return opt;
    }
}