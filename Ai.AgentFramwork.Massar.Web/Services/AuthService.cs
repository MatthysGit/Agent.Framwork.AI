using Ai.AgentFramwork.Massar.Web.DBModels;
using Ai.AgentFramwork.Massar.Web.Models;
using Ai.AgentFramwork.Massar.Web.Services.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Claims;
using static Ai.AgentFramwork.Massar.Web.DTO.LoginRequestDTO;

namespace Ai.AgentFramwork.Massar.Web.Services;

public interface IAuthService
{
    Task<(bool Success, string? ErrorMessage)> LoginAsync(string userOrEmail, string password, bool rememberMe);
    Task LogoutAsync();
}

public sealed class AuthService : IAuthService
{
    private readonly HttpClient _httpClient;
    public AuthService(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<(bool Success, string? ErrorMessage)> LoginAsync(string userOrEmail, string password, bool rememberMe)
    {
        var resp = await _httpClient.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(userOrEmail, password, rememberMe));

        var data = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        return (data?.Success == true, data?.ErrorMessage);
    }

    public async Task LogoutAsync()
    {
        await _httpClient.PostAsync("/api/auth/logout", null);
    }
}