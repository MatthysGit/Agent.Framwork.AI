using System.Text.Json.Serialization;

namespace Ai.AgentFramwork.Massar.Web.Models;

public class TableColumnDef
{
    [JsonPropertyName("table_name")] public string table_name { get; set; } = string.Empty;

    [JsonPropertyName("column_name")] public string column_name { get; set; } = string.Empty;

    [JsonPropertyName("data_type")] public string data_type { get; set; } = string.Empty;

    [JsonPropertyName("definition")] public string definition { get; set; } = string.Empty;
}