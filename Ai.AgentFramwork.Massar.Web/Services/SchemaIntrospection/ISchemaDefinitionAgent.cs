namespace Ai.AgentFramwork.Massar.Web.Services.SchemaIntrospection;

public interface ISchemaDefinitionAgent
{
    Task<(string TableDefinition, Dictionary<string, string> ColumnDefinitions)> GenerateDefinitionsAsync(
        string tableName,
        IReadOnlyList<(string Column, string SqlType)> columns,
        IReadOnlyDictionary<string, IReadOnlyList<string>> samples,
        CancellationToken ct);
}
