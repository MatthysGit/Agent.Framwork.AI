using Ai.AgentFramwork.Massar.ServiceDefaults.Helpers;
using Ai.AgentFramwork.Massar.Web.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.SqlClient;
using System.ComponentModel;
using System.Data;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ai.AgentFramwork.Massar.Web.Tools;

public sealed class SqlServerSelectTool
{
    private string _connectionString;
    private readonly IConfiguration _configuration;
    private readonly AuthenticationStateProvider _auth;

    public SqlServerSelectTool(IConfiguration configuration, AuthenticationStateProvider auth)
    {
        _configuration = configuration;
        _connectionString = _configuration["ConnectionStrings:AppDb"] ?? "";
        _auth = auth;
    }

    public async Task<int> GetMyGroupIdsAsync()
    {
        int _roleId = 0;

        var state = await _auth.GetAuthenticationStateAsync();
        var user = state.User;

        var roleClaim = user.FindFirst("RoleId");

        if (roleClaim != null && int.TryParse(roleClaim.Value, out var rid))
            _roleId = rid;

        return _roleId;
    }

    public async Task<string?> GetMyUserIdAsync()
    {
        var state = await _auth.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(AppClaimTypes.UserId);
    }

    [Description("Executes a SELECT-only SQL Server query and returns rows as JSON.")]
    public async Task<string> ExecuteSelectAsync(
        string sql,
        Dictionary<string, object?>? parameters = null,
        int maxRows = 200,
        int timeoutSeconds = 30)
    {
        ValidateSelectOnly(sql);

        // PATCH: fix SELECT column syntax
        sql = PatchSelectFields(sql);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn)
        {
            CommandType = CommandType.Text,
            CommandTimeout = timeoutSeconds
        };

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue("@" + name, value ?? DBNull.Value);
        }

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);

        var rows = new List<Dictionary<string, object?>>();
        int totalRead = 0;

        while (await reader.ReadAsync())
        {
            totalRead++;

            if (rows.Count >= maxRows)
                continue;

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);

            rows.Add(row);
        }

        return JsonSerializer.Serialize(new
        {
            rowCount = rows.Count,
            truncated = totalRead > maxRows,
            rows
        });
    }

    private static string PatchSelectFields(string sql)
    {
        var match = Regex.Match(sql,
            @"SELECT\s+(.*?)\s+FROM",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success)
            return sql;

        var selectPart = match.Groups[1].Value;

        var columns = selectPart.Split(',');

        for (int i = 0; i < columns.Length; i++)
        {
            var col = columns[i].Trim();

            // Skip expressions
            if (col.Contains("(") || col.Contains(")") || col.Contains("CASE", StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip already bracketed
            if (col.Contains("["))
                continue;

            // Match alias.column
            var dotMatch = Regex.Match(col, @"(\w+)\.(\w+)");

            if (dotMatch.Success)
            {
                var alias = dotMatch.Groups[1].Value;
                var field = dotMatch.Groups[2].Value;

                col = Regex.Replace(col,
                    @"(\w+)\.(\w+)",
                    $"{alias}.[{field}]");
            }
            else
            {
                // plain column
                col = Regex.Replace(col,
                    @"^\w+$",
                    m => $"[{m.Value}]");
            }

            columns[i] = col;
        }

        var patched = string.Join(", ", columns);

        return Regex.Replace(sql,
            @"SELECT\s+(.*?)\s+FROM",
            $"SELECT {patched} FROM",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    public static void ValidateSelectOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL is required.");

        var s = sql.Trim();

        if (!(s.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
              s.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Only SELECT queries are allowed.");

        string[] blocked =
        {
            "INSERT","UPDATE","DELETE","MERGE","DROP","ALTER","CREATE","TRUNCATE",
            "EXEC","EXECUTE","sp_","xp_","OPENROWSET","OPENDATASOURCE","BULK",
            "GRANT","REVOKE","DENY"
        };

        foreach (var token in blocked)
        {
            if (s.Contains(token, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Blocked keyword detected: {token}.");
        }
    }

    [Description("Gets tables and views available in the database")]
    public async Task<List<TableDef>> TableAndViewsInDatabse()
    {
        int secGroup = await GetMyGroupIdsAsync();
        List<TableDef> tables = new();

        using var connection = new SqlConnection(_configuration["ConnectionStrings:AI_DB"] ?? "");
        connection.Open();

        using SqlCommand cmd = connection.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "GetAITables";
        cmd.Parameters.AddWithValue("@SecGroup", secGroup);

        using SqlDataReader reader = cmd.ExecuteReader();
        tables = reader.MapToList<TableDef>();

        return tables;
    }

    [Description("Gets column definitions for a table")]
    public async Task<List<TableColumnDef>> TableColumsByTable(string table_name)
    {
        List<TableColumnDef> columns = new();
        int secGroup = await GetMyGroupIdsAsync();

        using var connection = new SqlConnection(_configuration["ConnectionStrings:AI_DB"] ?? "");
        connection.Open();

        using SqlCommand cmd = connection.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "GetAITaleColumnsByTableName";
        cmd.Parameters.AddWithValue("@table", table_name);
        cmd.Parameters.AddWithValue("@SecGroup", secGroup);

        using SqlDataReader reader = cmd.ExecuteReader();
        columns = reader.MapToList<TableColumnDef>();

        return columns;
    }

    [Description("Returns table relationships")]
    public async Task<List<TableColumnDef>> TableRelationships(string table_name)
    {
        List<TableColumnDef> columns = new();

        using var connection = new SqlConnection(_configuration["ConnectionStrings:AI_DB"] ?? "");
        connection.Open();

        using SqlCommand cmd = connection.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "GetAITablesReferencing";
        cmd.Parameters.AddWithValue("@table", table_name);

        using SqlDataReader reader = cmd.ExecuteReader();
        columns = reader.MapToList<TableColumnDef>();

        return columns;
    }
}