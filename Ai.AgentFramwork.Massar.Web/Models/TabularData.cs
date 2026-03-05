// File: Models/TabularData.cs
namespace Ai.AgentFramwork.Massar.Web.Models;

public sealed class TabularData
{
    public TabularData() { }

    public TabularData(string name, List<string> columns, List<IReadOnlyList<object?>> rows)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Spreadsheet" : name;
        Columns = columns ?? new List<string>();
        Rows = rows ?? new List<IReadOnlyList<object?>>();
    }

    // ✅ Overload to accept arrays / IReadOnlyList (SpreadsheetTableExtractor builds arrays)
    public TabularData(string name, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Spreadsheet" : name;
        Columns = columns?.ToList() ?? new List<string>();
        Rows = rows?.ToList() ?? new List<IReadOnlyList<object?>>();
    }

    public string Name { get; init; } = "Spreadsheet";
    public List<string> Columns { get; init; } = new();
    public List<IReadOnlyList<object?>> Rows { get; init; } = new();
}