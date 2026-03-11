using Ai.AgentFramwork.Massar.ServiceDefaults.Helpers;
using Ai.AgentFramwork.Massar.Web.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.ComponentModel;
using System.Data;
using System.Security.Claims;
using System.Text;
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
        if (string.IsNullOrWhiteSpace(sql))
            return sql;

        // Regex-based SQL rewriting is only safe for very simple single-select queries.
        // For CTEs, nested SELECTs, window functions, WITHIN GROUP, etc., return the SQL as-is.
        if (IsComplexQueryForSafePatching(sql))
            return sql;

        var aliases = ExtractTableAliases(sql);

        // Safe enough for simple queries: t.Name -> t.[Name]
        sql = PatchQualifiedIdentifiersSafe(sql, aliases);

        // Only patch the SELECT projection list for simple queries.
        // Do not patch WHERE/GROUP BY/HAVING/ORDER BY/ON using regex.
        sql = PatchClause(sql, "SELECT", new[] { "FROM" }, PatchSelectList);

        return sql;
    }

    private static bool IsComplexQueryForSafePatching(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        return Regex.IsMatch(
            sql,
            @"\bWITH\b|\bOVER\s*\(|\bWITHIN\s+GROUP\b|\(\s*SELECT\b|\bUNION\b|\bINTERSECT\b|\bEXCEPT\b",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static HashSet<string> ExtractTableAliases(string sql)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var matches = Regex.Matches(
            sql,
            @"\b(?:FROM|JOIN)\s+([A-Za-z_\[][A-Za-z0-9_\.\[\]]*)\s+(?:AS\s+)?(\[?[A-Za-z_][A-Za-z0-9_]*\]?)\b",
            RegexOptions.IgnoreCase);

        foreach (Match m in matches)
        {
            if (m.Groups.Count < 3)
                continue;

            var alias = m.Groups[2].Value.Trim();
            alias = Unbracket(alias);

            if (!string.IsNullOrWhiteSpace(alias))
                aliases.Add(alias);
        }

        return aliases;
    }

    private static string PatchQualifiedIdentifiersSafe(string sql, HashSet<string> aliases)
    {
        if (string.IsNullOrWhiteSpace(sql) || aliases.Count == 0)
            return sql;

        var spans = GetProtectedSpans(sql);
        var sb = new StringBuilder(sql.Length);

        int i = 0;
        while (i < sql.Length)
        {
            if (IsInsideProtectedSpan(i, spans, out var spanEnd))
            {
                sb.Append(sql, i, spanEnd - i);
                i = spanEnd;
                continue;
            }

            var match = Regex.Match(
                sql.AsSpan(i).ToString(),
                @"^(?<alias>[A-Za-z_][A-Za-z0-9_]*)\.(?<field>\[?[A-Za-z_][A-Za-z0-9_]*\]?)",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                sb.Append(sql[i]);
                i++;
                continue;
            }

            var alias = match.Groups["alias"].Value;
            var field = match.Groups["field"].Value;

            if (!aliases.Contains(alias))
            {
                sb.Append(sql[i]);
                i++;
                continue;
            }

            var replacement = $"{alias}.[{Unbracket(field)}]";
            sb.Append(replacement);
            i += match.Length;
        }

        return sb.ToString();
    }

    private static string PatchClause(
        string sql,
        string clauseStart,
        string[] clauseEnds,
        Func<string, string> patcher)
    {
        var pattern = BuildClausePattern(clauseStart, clauseEnds);

        return Regex.Replace(
            sql,
            pattern,
            m =>
            {
                var prefix = m.Groups["prefix"].Value;
                var body = m.Groups["body"].Value;
                var suffix = m.Groups["suffix"].Value;
                return prefix + patcher(body) + suffix;
            },
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static string BuildClausePattern(string clauseStart, string[] clauseEnds)
    {
        var endPattern = clauseEnds.Length == 0
            ? "$"
            : $@"(?=\b(?:{string.Join("|", clauseEnds.Select(Regex.Escape))})\b|$)";

        return $@"(?<prefix>\b{Regex.Escape(clauseStart)}\b\s+)(?<body>.*?)(?<suffix>\s*{endPattern})";
    }

    private static string PatchSelectList(string selectBody)
    {
        var parts = SplitTopLevel(selectBody, ',');

        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i].Trim();
            parts[i] = PatchStandaloneIdentifiersSafe(part, patchAliases: false);
        }

        return string.Join(", ", parts);
    }

    private static string PatchStandaloneIdentifiersSafe(string expression, bool patchAliases)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT","FROM","WHERE","GROUP","BY","HAVING","ORDER","ASC","DESC",
            "AND","OR","NOT","NULL","IS","IN","LIKE","BETWEEN","EXISTS",
            "CASE","WHEN","THEN","ELSE","END","AS","ON",
            "INNER","LEFT","RIGHT","FULL","OUTER","CROSS","JOIN",
            "TOP","DISTINCT","OFFSET","FETCH","NEXT","ROWS","ONLY",
            "SUM","AVG","MIN","MAX","COUNT","COALESCE","ISNULL","CAST","CONVERT",
            "YEAR","MONTH","DAY","DATEPART","DATEDIFF","DATEADD",
            "GETDATE","GETUTCDATE","SYSDATETIME","CURRENT_TIMESTAMP",
            "OVER","PARTITION","ROW_NUMBER","RANK","DENSE_RANK",
            "DECIMAL","NUMERIC","INT","BIGINT","SMALLINT","TINYINT","BIT",
            "FLOAT","REAL","MONEY","SMALLMONEY",
            "CHAR","NCHAR","VARCHAR","NVARCHAR","TEXT","NTEXT",
            "DATE","TIME","DATETIME","DATETIME2","SMALLDATETIME",
            "UNION","ALL","WITH","OFFSET","FETCH","NEXT","ROWS","ONLY",
            "NULLIF","IIF","TRY_CAST","TRY_CONVERT","ABS","ROUND","CEILING","FLOOR",
            "LOWER","UPPER","LTRIM","RTRIM","SUBSTRING","LEN","LEFT","RIGHT",
            "PERCENTILE_CONT","WITHIN"
        };

        var spans = GetProtectedSpans(expression);
        var sb = new StringBuilder(expression.Length);

        int i = 0;
        while (i < expression.Length)
        {
            if (IsInsideProtectedSpan(i, spans, out var spanEnd))
            {
                sb.Append(expression, i, spanEnd - i);
                i = spanEnd;
                continue;
            }

            if (!IsIdentifierStart(expression[i]))
            {
                sb.Append(expression[i]);
                i++;
                continue;
            }

            int start = i;
            i++;

            while (i < expression.Length && IsIdentifierPart(expression[i]))
                i++;

            var token = expression[start..i];

            if (keywords.Contains(token))
            {
                sb.Append(token);
                continue;
            }

            if (IsFunctionCall(expression, i))
            {
                sb.Append(token);
                continue;
            }

            if (IsAlreadyQualified(expression, start))
            {
                sb.Append(token);
                continue;
            }

            if (IsAlreadyBracketed(expression, start, i))
            {
                sb.Append(token);
                continue;
            }

            if (!patchAliases && IsAliasAfterAs(expression, start))
            {
                sb.Append(token);
                continue;
            }

            sb.Append('[').Append(token).Append(']');
        }

        return sb.ToString();
    }

    private static bool IsFunctionCall(string text, int tokenEnd)
    {
        int i = tokenEnd;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        return i < text.Length && text[i] == '(';
    }

    private static bool IsAlreadyQualified(string text, int tokenStart)
        => tokenStart > 0 && text[tokenStart - 1] == '.';

    private static bool IsAlreadyBracketed(string text, int tokenStart, int tokenEnd)
        => tokenStart > 0 &&
           tokenEnd < text.Length &&
           text[tokenStart - 1] == '[' &&
           text[tokenEnd] == ']';

    private static bool IsAliasAfterAs(string text, int tokenStart)
    {
        var left = text[..tokenStart];
        return Regex.IsMatch(left, @"\bAS\s*$", RegexOptions.IgnoreCase);
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string Unbracket(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        value = value.Trim();
        if (value.StartsWith("[", StringComparison.Ordinal) &&
            value.EndsWith("]", StringComparison.Ordinal) &&
            value.Length >= 2)
        {
            return value[1..^1];
        }

        return value;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            parts.Add(text);
            return parts;
        }

        var sb = new StringBuilder();
        int depth = 0;
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool inBracket = false;
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inLineComment)
            {
                sb.Append(ch);
                if (ch == '\n')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                sb.Append(ch);
                if (ch == '*' && next == '/')
                {
                    sb.Append(next);
                    i++;
                    inBlockComment = false;
                }
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && !inBracket)
            {
                if (ch == '-' && next == '-')
                {
                    sb.Append(ch).Append(next);
                    i++;
                    inLineComment = true;
                    continue;
                }

                if (ch == '/' && next == '*')
                {
                    sb.Append(ch).Append(next);
                    i++;
                    inBlockComment = true;
                    continue;
                }
            }

            if (!inDoubleQuote && !inBracket && ch == '\'')
            {
                sb.Append(ch);
                if (next == '\'')
                {
                    sb.Append(next);
                    i++;
                }
                else
                {
                    inSingleQuote = !inSingleQuote;
                }
                continue;
            }

            if (!inSingleQuote && !inBracket && ch == '"')
            {
                inDoubleQuote = !inDoubleQuote;
                sb.Append(ch);
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote)
            {
                if (ch == '[') inBracket = true;
                else if (ch == ']') inBracket = false;
                else if (!inBracket && ch == '(') depth++;
                else if (!inBracket && ch == ')') depth--;
            }

            if (!inSingleQuote && !inDoubleQuote && !inBracket && !inLineComment && !inBlockComment && depth == 0 && ch == separator)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }

        parts.Add(sb.ToString());
        return parts;
    }

    public static void ValidateSelectOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL is required.");

        var s = RemoveLeadingComments(sql).TrimStart();

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

    private static string RemoveLeadingComments(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return string.Empty;

        var s = sql;
        var i = 0;

        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
                i++;

            if (i >= s.Length)
                break;

            if (i + 1 < s.Length && s[i] == '-' && s[i + 1] == '-')
            {
                i += 2;
                while (i < s.Length && s[i] != '\n')
                    i++;
                continue;
            }

            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/'))
                    i++;

                if (i + 1 < s.Length)
                    i += 2;

                continue;
            }

            break;
        }

        return s[i..];
    }

    private static List<(int Start, int End)> GetProtectedSpans(string text)
    {
        var spans = new List<(int Start, int End)>();

        int i = 0;
        while (i < text.Length)
        {
            if (i + 1 < text.Length && text[i] == '-' && text[i + 1] == '-')
            {
                int start = i;
                i += 2;
                while (i < text.Length && text[i] != '\n')
                    i++;
                spans.Add((start, i));
                continue;
            }

            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                    i++;
                i = Math.Min(i + 2, text.Length);
                spans.Add((start, i));
                continue;
            }

            if (text[i] == '\'')
            {
                int start = i;
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                spans.Add((start, i));
                continue;
            }

            if (text[i] == '[')
            {
                int start = i;
                i++;
                while (i < text.Length && text[i] != ']')
                    i++;
                i = Math.Min(i + 1, text.Length);
                spans.Add((start, i));
                continue;
            }

            i++;
        }

        return spans;
    }

    private static bool IsInsideProtectedSpan(int index, List<(int Start, int End)> spans, out int spanEnd)
    {
        foreach (var span in spans)
        {
            if (index >= span.Start && index < span.End)
            {
                spanEnd = span.End;
                return true;
            }
        }

        spanEnd = -1;
        return false;
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
