namespace Ai.AgentFramwork.Massar.Web.Models;

public sealed class CurrentUserContext
{
    public string? UserId { get; private set; }
    public string? UserName { get; private set; }
    public IReadOnlyList<int> SecurityGroupIds { get; private set; } = Array.Empty<int>();
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(UserId);

    public void Set(string userId, string? userName, IEnumerable<int> groupIds)
    {
        UserId = userId;
        UserName = userName;
        SecurityGroupIds = groupIds.Distinct().OrderBy(x => x).ToArray();
    }

    public void Clear()
    {
        UserId = null;
        UserName = null;
        SecurityGroupIds = Array.Empty<int>();
    }
}