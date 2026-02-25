namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public sealed class DocumentSearchToolArgs
{
    public string Query { get; set; } = "";
    public int TopK { get; set; } = 5;
    public bool IncludeFilesIfAllowed { get; set; } = true;
}
