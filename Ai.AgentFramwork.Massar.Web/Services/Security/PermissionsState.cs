namespace Ai.AgentFramwork.Massar.Web.Services.Security;

public class PermissionsState
{
    public string? SelectedTableName { get; private set; }

    public event Action? Changed;

    public void SetSelectedTable(string? tableName)
    {
        SelectedTableName = tableName;
        Changed?.Invoke();
    }
}
