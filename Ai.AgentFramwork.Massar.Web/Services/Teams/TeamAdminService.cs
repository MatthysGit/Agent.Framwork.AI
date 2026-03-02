using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.EntityFrameworkCore;

namespace Ai.AgentFramwork.Massar.Web.Services.Teams;

public sealed class TeamAdminService : ITeamAdminService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public TeamAdminService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    #region Teams

    public async Task<IReadOnlyList<Team>> GetTeamsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<Team?> GetTeamAsync(int teamId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.TeamId == teamId, ct);
    }

    public async Task<int> CreateTeamAsync(string name, bool isActive, CancellationToken ct = default)
    {
        name = Normalize(name);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Team name is required.", nameof(name));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var dup = await db.Teams.AnyAsync(t => t.Name == name, ct);
        if (dup) throw new InvalidOperationException($"Team '{name}' already exists.");

        var entity = new Team { Name = name, IsActive = isActive, CreatedOn = DateTime.Now };
        db.Teams.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.TeamId;
    }

    public async Task UpdateTeamAsync(int teamId, string name, bool isActive, CancellationToken ct = default)
    {
        name = Normalize(name);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Team name is required.", nameof(name));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entity = await db.Teams.FirstOrDefaultAsync(t => t.TeamId == teamId, ct)
                     ?? throw new KeyNotFoundException("Team not found.");

        var dup = await db.Teams.AnyAsync(t => t.TeamId != teamId && t.Name == name, ct);
        if (dup) throw new InvalidOperationException($"Team '{name}' already exists.");

        entity.Name = name;
        entity.IsActive = isActive;

        await db.SaveChangesAsync(ct);
    }

    public async Task SetTeamActiveAsync(int teamId, bool isActive, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Teams.FirstOrDefaultAsync(t => t.TeamId == teamId, ct)
                     ?? throw new KeyNotFoundException("Team not found.");

        entity.IsActive = isActive;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteTeamAsync(int teamId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Delete links first unless cascade is configured.
        var links = await db.TeamRoles.Where(x => x.TeamId == teamId).ToListAsync(ct);
        if (links.Count > 0) db.TeamRoles.RemoveRange(links);

        var entity = await db.Teams.FirstOrDefaultAsync(t => t.TeamId == teamId, ct)
                     ?? throw new KeyNotFoundException("Team not found.");

        db.Teams.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    #endregion

    #region Roles catalog

    public async Task<IReadOnlyList<Role>> SearchRolesAsync(string? search, int take = 20, CancellationToken ct = default)
    {
        search = Normalize(search);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        IQueryable<Role> q = db.Roles.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(r => r.Name.Contains(search));

        return await q.OrderBy(r => r.Name).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
    }

    public async Task DeleteRoleAsync(int roleId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var hasLinks = await db.TeamRoles.AnyAsync(tr => tr.RoleId == roleId, ct);
        if (hasLinks) throw new InvalidOperationException("Role is linked to teams. Remove links first.");

        var role = await db.Roles.FindAsync(new object[] { roleId }, ct);
        if (role is null) return;

        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Role?> GetRoleAsync(int roleId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.RoleId == roleId, ct);
    }

    public async Task<int> CreateRoleAsync(string name, bool isActive, bool privilegedPpi, CancellationToken ct = default)
    {
        name = Normalize(name);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Role name is required.", nameof(name));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var dup = await db.Roles.AnyAsync(r => r.Name == name, ct);
        if (dup) throw new InvalidOperationException($"Role '{name}' already exists.");

        var entity = new Role { Name = name, IsActive = isActive, CreatedOn = DateTime.Now, PrivilegedPPI = privilegedPpi };
        db.Roles.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.RoleId;
    }

    public async Task UpdateRoleAsync(int roleId, string name, bool isActive, bool privilegedPpi, CancellationToken ct = default)
    {
        name = Normalize(name);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Role name is required.", nameof(name));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entity = await db.Roles.FirstOrDefaultAsync(r => r.RoleId == roleId, ct)
                     ?? throw new KeyNotFoundException("Role not found.");

        var dup = await db.Roles.AnyAsync(r => r.RoleId != roleId && r.Name == name, ct);
        if (dup) throw new InvalidOperationException($"Role '{name}' already exists.");

        entity.Name = name;
        entity.IsActive = isActive;
        entity.PrivilegedPPI= privilegedPpi;

        await db.SaveChangesAsync(ct);
    }

    #endregion

    #region TeamRole links

    public async Task<IReadOnlyList<TeamRole>> GetTeamRolesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TeamRoles.AsNoTracking()
            .Include(tr => tr.Role)
            .OrderBy(tr => tr.TeamId)
            .ThenBy(tr => tr.Role.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TeamRole>> GetTeamRolesForTeamAsync(int teamId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TeamRoles.AsNoTracking()
            .Where(tr => tr.TeamId == teamId)
            .Include(tr => tr.Role)
            .OrderBy(tr => tr.Role.Name)
            .ToListAsync(ct);
    }

    public async Task<int> AddRoleToTeamByNameAsync(int teamId, string roleName, bool isActive = true, CancellationToken ct = default)
    {
        roleName = Normalize(roleName);
        if (string.IsNullOrWhiteSpace(roleName)) throw new ArgumentException("Role is required.", nameof(roleName));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var teamExists = await db.Teams.AnyAsync(t => t.TeamId == teamId, ct);
        if (!teamExists) throw new KeyNotFoundException("Team not found.");

        // Find-or-create role (master list)
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName, ct);
        if (role is null)
        {
            role = new Role { Name = roleName, IsActive = true, CreatedOn = DateTime.Now };
            db.Roles.Add(role);
            await db.SaveChangesAsync(ct);
        }

        // Ensure link exists
        var existing = await db.TeamRoles.FirstOrDefaultAsync(tr => tr.TeamId == teamId && tr.RoleId == role.RoleId, ct);
        if (existing is not null)
        {
            // Optionally update link active flag when re-adding
            existing.IsActive = isActive;
            await db.SaveChangesAsync(ct);
            return existing.TeamRoleId;
        }

        var link = new TeamRole
        {
            TeamId = teamId,
            RoleId = role.RoleId,
            IsActive = isActive,
            CreatedOn = DateTime.Now
        };

        db.TeamRoles.Add(link);
        await db.SaveChangesAsync(ct);
        return link.TeamRoleId;
    }

    public async Task SetTeamRoleActiveAsync(int teamRoleId, bool isActive, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var link = await db.TeamRoles.FirstOrDefaultAsync(tr => tr.TeamRoleId == teamRoleId, ct)
                   ?? throw new KeyNotFoundException("Team role link not found.");

        link.IsActive = isActive;
        await db.SaveChangesAsync(ct);
    }

    public async Task<TeamRole?> GetTeamRoleAsync(int teamRoleId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        return await db.TeamRoles
            .AsNoTracking()
            .Include(tr => tr.Role)
            .FirstOrDefaultAsync(tr => tr.TeamRoleId == teamRoleId, ct);
    }

    public async Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        return await db.Roles
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
    }

    public async Task RemoveRoleFromTeamAsync(int teamRoleId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var link = await db.TeamRoles.FirstOrDefaultAsync(tr => tr.TeamRoleId == teamRoleId, ct)
                   ?? throw new KeyNotFoundException("Team role link not found.");

        db.TeamRoles.Remove(link);
        await db.SaveChangesAsync(ct);
    }

    #endregion

    private static string Normalize(string? v) => (v ?? string.Empty).Trim();
}
