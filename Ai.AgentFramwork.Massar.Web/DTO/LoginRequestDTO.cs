namespace Ai.AgentFramwork.Massar.Web.DTO
{
    public class LoginRequestDTO
    {
        public record LoginRequest(string UserOrEmail, string Password, bool RememberMe);
        public record LoginResponse(bool Success, string? ErrorMessage);

    }
}
