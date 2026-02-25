using Ai.AgentFramwork.Massar.ServiceDefaults.Helpers;
using Ai.AgentFramwork.Massar.Web.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.SqlClient;
using System.ComponentModel;
using System.Data;
using System.Security.Claims;
using System.Text.Json;

namespace Ai.AgentFramwork.Massar.Web.Tools;

public sealed class SqlServerSelectTool
{
    private  string _connectionString;
    private readonly IConfiguration _configuration;
    private readonly AuthenticationStateProvider _auth;

    public  SqlServerSelectTool(IConfiguration configuration, AuthenticationStateProvider auth)
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

        foreach (var claim in user.Claims)
        {
            Console.WriteLine($"{claim.Type} = {claim.Value}");
        }

        

        if (roleClaim != null && int.TryParse(roleClaim.Value, out var rid))
        {
            _roleId = rid;
        }

        return _roleId;


        //if (user?.Identity?.IsAuthenticated != true)
        //  return Array.Empty<int>();

        //return user.FindAll(AppClaimTypes.SecurityGroupId)
        //    .Select(c => int.TryParse(c.Value, out var v) ? v : (int?)null)
        //    .Where(v => v.HasValue)
        //    .Select(v => v!.Value)
        //    .ToArray();
    }

    public async Task<string?> GetMyUserIdAsync()
    {
        var state = await _auth.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(AppClaimTypes.UserId);
    }


    [Description("Executes a SELECT-only SQL Server query and returns rows as JSON. Parameters must be provided separately.")]
    public async Task<string> ExecuteSelectAsync(
        [Description("A SELECT-only query (optionally starting with WITH for CTEs). No semicolons; single statement only.")]
        string sql,
        [Description("Named parameters. Keys should match parameter names without '@'.")]
        Dictionary<string, object?>? parameters = null,
        [Description("Max rows returned (defensive cap).")]
        int maxRows = 200,
        [Description("Command timeout in seconds.")]
        int timeoutSeconds = 30)
    {
         ValidateSelectOnly(sql);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn)
        {
            CommandType = CommandType.Text,
            CommandTimeout = timeoutSeconds
        };

        if (parameters is not null)
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue("@" + name, value ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);

        var rows = new List<Dictionary<string, object?>>();
        int totalRead = 0;

        while (await reader.ReadAsync())
        {
            totalRead++;
            if (rows.Count >= maxRows) continue;

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);

            rows.Add(row);
        }

        return JsonSerializer.Serialize(new { rowCount = rows.Count, truncated = totalRead > maxRows, rows });
    }

    public static void ValidateSelectOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("SQL is required.");
        var s = sql.Trim();
        //if (s.Contains(';')) throw new ArgumentException("Semicolons are not allowed (single SELECT only).");

        if (!(s.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
              s.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Only SELECT queries are allowed (CTEs starting with WITH are allowed).");

        string[] blocked =
        {
            "INSERT","UPDATE","DELETE","MERGE","DROP","ALTER","CREATE","TRUNCATE",
            "EXEC","EXECUTE","sp_","xp_","OPENROWSET","OPENDATASOURCE","BULK",
            "GRANT","REVOKE","DENY"
        };

        foreach (var token in blocked)
            if (s.Contains(token, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Blocked keyword detected: {token}.");
    }


    [Description("Gets the tables and view avalible in the databse that can be used in ExecuteSelectAsync")]
    public async Task<List<TableDef>> TableAndViewsInDatabse() 
    {
        int secGroup = await GetMyGroupIdsAsync();

        List<TableDef> tables = new List<TableDef>();

        using (var connection = new SqlConnection(_configuration["ConnectionStrings:AI_DB"] ?? ""))
        {
            connection.Open();
            using (SqlCommand cmd = connection.CreateCommand())
            {
                cmd.CommandType = System.Data.CommandType.StoredProcedure;
                cmd.CommandText = "GetAITables";
                cmd.Parameters.AddWithValue("@SecGroup", secGroup); // variable holding value

                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    tables = reader.MapToList<TableDef>();
                }
            }
            connection.Close();
        }

        return tables;   
    }

    [Description("Gets the table colums definitions for a table, column_name, data_type and definition")]
    public async Task<List<TableColumnDef>> TableColumsByTable(string table_name)
    {
        List<TableColumnDef> columns = new List<TableColumnDef>();
        int secGroup = await GetMyGroupIdsAsync();

        using (var connection = new SqlConnection(_configuration["ConnectionStrings:AI_DB"] ?? ""))
        {
            connection.Open();
            using (SqlCommand cmd = connection.CreateCommand())
            {
                cmd.CommandType = System.Data.CommandType.StoredProcedure;
                cmd.CommandText = "GetAITaleColumnsByTableName";
                cmd.Parameters.AddWithValue("@table", table_name);
                cmd.Parameters.AddWithValue("@SecGroup", secGroup);

                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    columns = reader.MapToList<TableColumnDef>();
                }
            }
            connection.Close();
        }

        return columns;
    }


    [Description("TableRelationships to understand the relationships between tables")]
    public async Task<List<TableColumnDef>> TableRelationships(string table_name)
    {
        List<TableColumnDef> columns = new List<TableColumnDef>();

        using (var connection = new SqlConnection(_configuration["ConnectionStrings:AI_DB"] ?? ""))
        {
            connection.Open();
            using (SqlCommand cmd = connection.CreateCommand())
            {
                cmd.CommandType = System.Data.CommandType.StoredProcedure;
                cmd.CommandText = "GetAITablesReferencing";
                cmd.Parameters.AddWithValue("@table", table_name); // variable holding value

                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    columns = reader.MapToList<TableColumnDef>();
                }
            }
            connection.Close();
        }

        return columns;
    }


}