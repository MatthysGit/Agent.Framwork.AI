using Ai.AgentFramwork.Massar.Web.DBModels;

namespace Ai.AgentFramwork.Massar.Web.Services.Teams;

public interface ITeamAdminService
{
    // Teams
    Task<IReadOnlyList<Team>> GetTeamsAsync(CancellationToken ct = default);
    Task<Team?> GetTeamAsync(int teamId, CancellationToken ct = default);
    Task<int> CreateTeamAsync(string name, bool isActive, CancellationToken ct = default);
    Task UpdateTeamAsync(int teamId, string name, bool isActive, CancellationToken ct = default);
    Task SetTeamActiveAsync(int teamId, bool isActive, CancellationToken ct = default);
    Task DeleteTeamAsync(int teamId, CancellationToken ct = default);

    // Roles catalog
    Task<IReadOnlyList<Role>> SearchRolesAsync(string? search, int take = 20, CancellationToken ct = default);
    Task<Role?> GetRoleAsync(int roleId, CancellationToken ct = default);
    Task<int> CreateRoleAsync(string name, bool isActive, bool privilegedPpi, CancellationToken ct = default);
    Task UpdateRoleAsync(int roleId, string name, bool isActive, bool privilegedPpi, CancellationToken ct = default);
    Task<TeamRole?> GetTeamRoleAsync(int teamRoleId, CancellationToken ct = default);
    // TeamRole links
    Task<IReadOnlyList<TeamRole>> GetTeamRolesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TeamRole>> GetTeamRolesForTeamAsync(int teamId, CancellationToken ct = default);
    Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken ct = default);
    /// <summary>
    /// Adds an existing role (or creates it if missing) to the team, by role name.
    /// This is used by the autocomplete free-text UX to prevent typos and duplicates.
    /// </summary>
    Task<int> AddRoleToTeamByNameAsync(int teamId, string roleName, bool isActive = true, CancellationToken ct = default);

    Task SetTeamRoleActiveAsync(int teamRoleId, bool isActive, CancellationToken ct = default);
    Task RemoveRoleFromTeamAsync(int teamRoleId, CancellationToken ct = default);

    Task DeleteRoleAsync(int roleId, CancellationToken ct = default);
}
