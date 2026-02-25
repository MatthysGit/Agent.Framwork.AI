using Ai.AgentFramwork.Massar.Web.Models;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;



namespace Ai.AgentFramwork.Massar.Web.Services;

public sealed class CurrentUserBootstrapper
{
    private readonly AuthenticationStateProvider _auth;
    private readonly CurrentUserContext _me;

    public CurrentUserBootstrapper(AuthenticationStateProvider auth, CurrentUserContext me)
    {
        _auth = auth;
        _me = me;
    }

    public async Task EnsureLoadedAsync()
    {
        if (_me.IsAuthenticated) return;

        var state = await _auth.GetAuthenticationStateAsync();
        var user = state.User;

        if (user?.Identity?.IsAuthenticated != true)
        {
            _me.Clear();
            return;
        }

        var userId = user.FindFirstValue(AppClaimTypes.UserId);
        var userName = user.Identity?.Name;

        var groupIds = user.FindAll(AppClaimTypes.SecurityGroupId)
            .Select(c => int.TryParse(c.Value, out var v) ? v : (int?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(userId))
            _me.Set(userId!, userName, groupIds);
    }
}
