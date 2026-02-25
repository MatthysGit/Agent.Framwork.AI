using System.Text.Json.Serialization;

namespace Ai.AgentFramwork.Massar.Web.Models;

public class TableReferencing
{
    [JsonPropertyName("referencing_table_name")]
    public string referencing_table_name { get; set; } = string.Empty;

    [JsonPropertyName("referencing_column_name")]
    public string referencing_column_name { get; set; } = string.Empty;
}