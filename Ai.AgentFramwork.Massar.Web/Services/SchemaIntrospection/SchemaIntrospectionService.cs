using System.Data;
using Ai.AgentFramwork.Massar.Web.DBModels;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ai.AgentFramwork.Massar.Web.Services.SchemaIntrospection;

public sealed class SchemaIntrospectionService : ISchemaIntrospectionService
{
    private readonly AppDbContext _db;
    private readonly ISchemaDefinitionAgent _agent;
    private readonly IConfiguration _configuration;

    public SchemaIntrospectionService(AppDbContext db, ISchemaDefinitionAgent agent, IConfiguration configuration)
    {
        _db = db;
        _agent = agent;
        _configuration = configuration;
    }

    public async Task RunAsync(IProgress<SchemaIntrospectionProgress> progress, CancellationToken ct)
    {
        progress.Report(new("Init", "Starting schema introspection..."));

        var conn = _db.Database.GetDbConnection();
        SqlConnection connDataDB = new SqlConnection(_configuration["ConnectionStrings:AppDb"] ?? "");



        if (conn is not SqlConnection sqlConn)
            throw new InvalidOperationException("This tool currently supports SQL Server only (SqlConnection expected).");

        if (sqlConn.State != ConnectionState.Open)
            await sqlConn.OpenAsync(ct);

        // 1) Discover tables and columns
        progress.Report(new("Schema", "Reading tables & columns from SQL Server..."));
        var tables = await LoadUserTablesAsync(connDataDB, ct);
        var columnsByTable = await LoadColumnsAsync(connDataDB, tables, ct);

        // 2) Sync catalog tables/fields (add missing / remove stale / update datatype)
        progress.Report(new("Catalog", "Syncing TableDefinition / TableFieldDefinition..."));
        await SyncCatalogAsync(tables, columnsByTable, progress, ct);

        // 3) Rebuild TableReferencing from FK metadata
        progress.Report(new("Relationships", "Rebuilding TableReferencing from foreign keys..."));
        await RebuildReferencingAsync(connDataDB, ct);

        // 4) For each table: sample top 12 rows + call agent to update definitions
        progress.Report(new("Enrichment", "Analyzing TOP 12 rows per table to enrich definitions..."));

        var total = tables.Count;
        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();

            var table = tables[i]; // already in "schema.table" format
            progress.Report(new("Enrichment", $"Sampling data: {table}", i + 1, total));

            var cols = columnsByTable[table];

            // Only enrich missing definitions (reruns should not overwrite curated definitions)
            var needsTableDef = await _db.TableDefinitions
                .AnyAsync(x => x.Name == table && (x.Definition == null || x.Definition == string.Empty), ct);

            var needsAnyFieldDef = await _db.TableFieldDefinitions
                .AnyAsync(x => x.table_name == table && (x.definition == null || x.definition == string.Empty), ct);

            if (!needsTableDef && !needsAnyFieldDef)
            {
                progress.Report(new("Enrichment", $"Skipping (already defined): {table}", i + 1, total));
                continue;
            }

            var samples = await LoadSamplesAsync(connDataDB, table, cols, ct);

            progress.Report(new("Agent", $"Generating definitions via agent: {table}", i + 1, total));

            var (tableDef, colDefs) = await _agent.GenerateDefinitionsAsync(
                tableName: table,
                columns: cols.Select(c => (c.ColumnName, c.SqlType)).ToList(),
                samples: samples,
                ct: ct);

            await ApplyDefinitionsAsync(table, tableDef, colDefs, cols, overwriteExisting: false, ct);

            progress.Report(new("Enrichment", $"Updated definitions: {table}", i + 1, total));
        }

        progress.Report(new("Done", "Schema introspection completed."));
    }

    // ---------- SQL Metadata ----------

    private sealed record TableCol(string ColumnName, string SqlType);

    private static async Task<List<string>> LoadUserTablesAsync(SqlConnection conn, CancellationToken ct)
    {
       

        // Avoid system schemas; include dbo + app schemas + AdventureWorks schemas (Sales, Production, HumanResources, Purchasing, Person, etc.)
        const string sql = """
SELECT CONCAT(s.name, '.', t.name) AS FullName
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.is_ms_shipped = 0
  AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
ORDER BY s.name, t.name;
""";

        conn.Open();

        using var cmd = new SqlCommand(sql, conn);
        using var rdr = await cmd.ExecuteReaderAsync(ct);

        var list = new List<string>();
        while (await rdr.ReadAsync(ct))
            list.Add(rdr.GetString(0));

        // Optional: exclude your own metadata tables if they are in same DB
        list.RemoveAll(t =>
            t.Equals("dbo.TableDefinition", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("dbo.TableFieldDefinition", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("dbo.TableReferencing", StringComparison.OrdinalIgnoreCase));

        conn.Close();

        return list;
    }

    private static async Task<Dictionary<string, List<TableCol>>> LoadColumnsAsync(
        SqlConnection conn,
        IReadOnlyList<string> tables,
        CancellationToken ct)
    {
        // Pull all columns for user tables
        const string sql = """
SELECT
  CONCAT(s.name, '.', t.name) AS FullName,
  c.name AS ColumnName,
  ty.name AS SqlType,
  c.max_length,
  c.precision,
  c.scale
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.columns c ON c.object_id = t.object_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE t.is_ms_shipped = 0
  AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
ORDER BY FullName, c.column_id;
""";

        var set = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);

        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        using var rdr = await cmd.ExecuteReaderAsync(ct);

        var dict = new Dictionary<string, List<TableCol>>(StringComparer.OrdinalIgnoreCase);

        while (await rdr.ReadAsync(ct))
        {
            var full = rdr.GetString(0);
            if (!set.Contains(full)) continue;

            var col = rdr.GetString(1);
            var type = rdr.GetString(2);

            // Keep type readable; you can extend to include length/precision if you want
            dict.TryAdd(full, new List<TableCol>());
            dict[full].Add(new TableCol(col, type));
        }

        // Ensure every table has an entry
        foreach (var t in tables)
            dict.TryAdd(t, new List<TableCol>());

        conn.Close();

        return dict;
    }

    // ---------- Catalog Sync (rerunnable) ----------

    private async Task SyncCatalogAsync(
        IReadOnlyList<string> dbTables,
        IReadOnlyDictionary<string, List<TableCol>> dbColumnsByTable,
        IProgress<SchemaIntrospectionProgress> progress,
        CancellationToken ct)
    {
        // Load catalog tables
        var catTables = await _db.TableDefinitions.AsNoTracking()
            .Select(t => t.Name)
            .ToListAsync(ct);

        var catTableSet = new HashSet<string>(catTables, StringComparer.OrdinalIgnoreCase);
        var dbTableSet = new HashSet<string>(dbTables, StringComparer.OrdinalIgnoreCase);

        // Remove stale tables (and dependent rows)
        var removedTables = catTableSet.Except(dbTableSet).ToList();
        if (removedTables.Count > 0)
        {
            progress.Report(new("Catalog", $"Removing {removedTables.Count} stale tables..."));

            var staleFields = await _db.TableFieldDefinitions
                .Where(f => removedTables.Contains(f.table_name))
                .ToListAsync(ct);
            _db.TableFieldDefinitions.RemoveRange(staleFields);

            var staleRefs = await _db.TableReferencings
                .Where(r => removedTables.Contains(r.table_name) || removedTables.Contains(r.referencing_table_name))
                .ToListAsync(ct);
            _db.TableReferencings.RemoveRange(staleRefs);

            var staleTableDefs = await _db.TableDefinitions
                .Where(t => removedTables.Contains(t.Name))
                .ToListAsync(ct);
            _db.TableDefinitions.RemoveRange(staleTableDefs);

            await _db.SaveChangesAsync(ct);
        }

        // Add missing tables
        var missingTables = dbTableSet.Except(catTableSet).ToList();
        if (missingTables.Count > 0)
        {
            progress.Report(new("Catalog", $"Adding {missingTables.Count} missing tables..."));
            foreach (var t in missingTables)
            {
                _db.TableDefinitions.Add(new TableDefinition { Name = t, Definition = null });
            }
            await _db.SaveChangesAsync(ct);
        }

        // Load catalog fields (after table sync)
        var catFields = await _db.TableFieldDefinitions.AsNoTracking()
            .Select(f => new { f.table_name, f.column_name, f.data_type })
            .ToListAsync(ct);

        var catFieldSet = new HashSet<(string Table, string Column)>(
            catFields.Select(x => (x.table_name, x.column_name)));

        var dbFieldSet = new HashSet<(string Table, string Column)>();
        foreach (var kv in dbColumnsByTable)
            foreach (var c in kv.Value)
                dbFieldSet.Add((kv.Key, c.ColumnName));

        // Remove stale fields
        var removedFields = catFieldSet.Except(dbFieldSet).ToList();
        if (removedFields.Count > 0)
        {
            progress.Report(new("Catalog", $"Removing {removedFields.Count} stale fields..."));

            // EF doesn't translate tuple sets well; pull candidates by table then filter in-memory
            var tablesToCheck = removedFields.Select(x => x.Table).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var candidates = await _db.TableFieldDefinitions
                .Where(f => tablesToCheck.Contains(f.table_name))
                .ToListAsync(ct);

            var removedSet = new HashSet<(string, string)>(removedFields);
            var toDelete = candidates.Where(f => removedSet.Contains((f.table_name, f.column_name))).ToList();

            _db.TableFieldDefinitions.RemoveRange(toDelete);
            await _db.SaveChangesAsync(ct);
        }

        // Add missing fields
        var missingFields = dbFieldSet.Except(catFieldSet).ToList();
        if (missingFields.Count > 0)
        {
            progress.Report(new("Catalog", $"Adding {missingFields.Count} missing fields..."));

            foreach (var (table, col) in missingFields)
            {
                var dataType = dbColumnsByTable[table]
                    .First(x => x.ColumnName.Equals(col, StringComparison.OrdinalIgnoreCase))
                    .SqlType;

                _db.TableFieldDefinitions.Add(new TableFieldDefinition
                {
                    table_name = table,
                    column_name = col,
                    data_type = dataType,
                    definition = null
                });
            }
            await _db.SaveChangesAsync(ct);
        }

        // Update datatype for changed fields
        var catTypeLookup = catFields.ToDictionary(
            x => (x.table_name, x.column_name),
            x => x.data_type ?? string.Empty);

        var changed = new List<(string Table, string Column, string NewType)>();
        foreach (var kv in dbColumnsByTable)
        {
            foreach (var c in kv.Value)
            {
                var key = (kv.Key, c.ColumnName);
                if (catTypeLookup.TryGetValue(key, out var oldType)
                    && !string.Equals(oldType, c.SqlType, StringComparison.OrdinalIgnoreCase))
                {
                    changed.Add((kv.Key, c.ColumnName, c.SqlType));
                }
            }
        }

        if (changed.Count > 0)
        {
            progress.Report(new("Catalog", $"Updating datatype for {changed.Count} fields..."));
            foreach (var (table, col, newType) in changed)
            {
                var row = await _db.TableFieldDefinitions
                    .FirstAsync(x => x.table_name == table && x.column_name == col, ct);
                row.data_type = newType;
            }
            await _db.SaveChangesAsync(ct);
        }

        progress.Report(new("Catalog", "Catalog sync complete."));
    }

    private async Task RebuildReferencingAsync(SqlConnection conn, CancellationToken ct)
    {
        // 1) Materialize all FK rows first (reader must be closed before EF saves)
        var rows = new List<(string table, string refTable, string refColumn)>();

        const string sql = """
SELECT
  CONCAT(sRef.name, '.', tRef.name) AS ReferencedTable,
  CONCAT(sPar.name, '.', tPar.name) AS ReferencingTable,
  cPar.name AS ReferencingColumn
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables tPar ON tPar.object_id = fkc.parent_object_id
JOIN sys.schemas sPar ON sPar.schema_id = tPar.schema_id
JOIN sys.columns cPar ON cPar.object_id = tPar.object_id AND cPar.column_id = fkc.parent_column_id
JOIN sys.tables tRef ON tRef.object_id = fkc.referenced_object_id
JOIN sys.schemas sRef ON sRef.schema_id = tRef.schema_id
WHERE fk.is_ms_shipped = 0
ORDER BY ReferencedTable, ReferencingTable;
""";

        conn.Open();

        using (var cmd = new SqlCommand(sql, conn))
        using (var rdr = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rdr.ReadAsync(ct))
            {
                rows.Add((
                    table: rdr.GetString(0),
                    refTable: rdr.GetString(1),
                    refColumn: rdr.GetString(2)
                ));
            }
        } // ✅ reader closed here

        // 2) Now do EF work
        _db.TableReferencings.RemoveRange(_db.TableReferencings);
        await _db.SaveChangesAsync(ct);

        var entities = rows.Select(r => new TableReferencing
        {
            table_name = r.table,
            referencing_table_name = r.refTable,
            referencing_column_name = r.refColumn
        }).ToList();

        _db.TableReferencings.AddRange(entities);

        conn.Close();
        await _db.SaveChangesAsync(ct);
    }

    // ---------- Sampling + Enrichment ----------

    private static async Task<Dictionary<string, IReadOnlyList<string>>> LoadSamplesAsync(
        SqlConnection conn,
        string fullTableName,
        IReadOnlyList<TableCol> cols,
        CancellationToken ct)
    {
        // Minimal sampling: top 100, then for each column keep up to ~12 distinct non-null stringified values.
        // This keeps tokens under control while still letting the agent infer meaning.
        var (schema, table) = SplitFullName(fullTableName);

        // Select TOP 12 * (quoted)
        var sql = $"SELECT TOP (12) * FROM {Quote(schema)}.{Quote(table)};";

        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        using var rdr = await cmd.ExecuteReaderAsync(ct);

        var sampleSets = cols.ToDictionary(
            c => c.ColumnName,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        while (await rdr.ReadAsync(ct))
        {
            for (var i = 0; i < rdr.FieldCount; i++)
            {
                var colName = rdr.GetName(i);
                if (!sampleSets.TryGetValue(colName, out var set)) continue;

                if (rdr.IsDBNull(i)) continue;

                var val = rdr.GetValue(i)?.ToString();
                if (string.IsNullOrWhiteSpace(val)) continue;

                val = SanitizeSample(val);
                if (val.Length == 0) continue;

                if (set.Count < 12)
                    set.Add(val);
            }
        }
        conn.Close();

        return sampleSets.ToDictionary(
            k => k.Key,
            v => (IReadOnlyList<string>)v.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task ApplyDefinitionsAsync(
        string fullTableName,
        string tableDefinition,
        Dictionary<string, string> columnDefinitions,
        IReadOnlyList<TableCol> columns,
        bool overwriteExisting,
        CancellationToken ct)
    {
        // Update TableDefinition.Definition
        var td = await _db.TableDefinitions.FirstAsync(x => x.Name == fullTableName, ct);
        if (!string.IsNullOrWhiteSpace(tableDefinition))
        {
            if (overwriteExisting || string.IsNullOrWhiteSpace(td.Definition))
                td.Definition = tableDefinition.Trim();
        }

        // Update TableFieldDefinition.definition
        foreach (var c in columns)
        {
            if (!columnDefinitions.TryGetValue(c.ColumnName, out var def)) continue;
            if (string.IsNullOrWhiteSpace(def)) continue;

            var tfd = await _db.TableFieldDefinitions
                .FirstAsync(x => x.table_name == fullTableName && x.column_name == c.ColumnName, ct);

            if (overwriteExisting || string.IsNullOrWhiteSpace(tfd.definition))
                tfd.definition = def.Trim();
        }

        await _db.SaveChangesAsync(ct);
    }

    // ---------- Helpers ----------

    private static (string Schema, string Table) SplitFullName(string full)
    {
        var idx = full.IndexOf('.');
        if (idx <= 0 || idx >= full.Length - 1)
            return ("dbo", full);

        return (full[..idx], full[(idx + 1)..]);
    }

    private static string Quote(string ident) => $"[{ident.Replace("]", "]]")}]";

    private static string SanitizeSample(string s)
    {
        // Keep it safe / token-friendly
        s = s.Trim();
        if (s.Length > 80) s = s[..80] + "…";
        // Prevent multi-line blowup
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        return s;
    }
}
