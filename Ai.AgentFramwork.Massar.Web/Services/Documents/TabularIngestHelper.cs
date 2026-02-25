namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

using System.Data;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ExcelDataReader;
using Microsoft.VisualBasic.FileIO;

public static class TabularIngestHelper
{
    public sealed record SheetTable(string SheetName, List<string[]> Rows);

    // Hard bounds (tune)
    private const int MaxSheets = 6;
    private const int MaxRowsPerSheet = 2000;
    private const int MaxCellsPerRow = 40;
    private const int MaxCellChars = 250;

    public static bool IsTabular(string fileName, string contentType)
    {
        var ext = (Path.GetExtension(fileName) ?? "").ToLowerInvariant();
        contentType ??= "";

        return ext is ".xlsx" or ".xls" or ".csv"
               || string.Equals(contentType, "text/csv", StringComparison.OrdinalIgnoreCase)
               || string.Equals(contentType, "application/csv", StringComparison.OrdinalIgnoreCase)
               || string.Equals(contentType, "application/vnd.ms-excel", StringComparison.OrdinalIgnoreCase)
               || string.Equals(contentType, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase);
    }

    public static List<SheetTable> ParseTabular(byte[] bytes, string fileName, string contentType)
    {
        var ext = (Path.GetExtension(fileName) ?? "").ToLowerInvariant();
        contentType ??= "";

        if (ext == ".csv" || string.Equals(contentType, "text/csv", StringComparison.OrdinalIgnoreCase) || string.Equals(contentType, "application/csv", StringComparison.OrdinalIgnoreCase))
            return new List<SheetTable> { new SheetTable("CSV", ParseCsv(bytes)) };

        if (ext == ".xlsx" || string.Equals(contentType, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase))
            return ParseXlsxBounded(bytes);

        if (ext == ".xls" || string.Equals(contentType, "application/vnd.ms-excel", StringComparison.OrdinalIgnoreCase))
            return ParseXlsBounded(bytes);

        return new List<SheetTable>();
    }

    // ---------------- XLSX (bounded) ----------------

    private static List<SheetTable> ParseXlsxBounded(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = SpreadsheetDocument.Open(ms, false);

        var wbPart = doc.WorkbookPart;
        if (wbPart?.Workbook == null)
            return new List<SheetTable>();

        var sst = wbPart.SharedStringTablePart?.SharedStringTable;

        string GetCellText(Cell? cell)
        {
            if (cell == null) return "";

            // Fast path: inline string
            if (cell.DataType?.Value == CellValues.InlineString)
            {
                var t = cell.InnerText ?? "";
                return Trunc(t);
            }

            var value = cell.CellValue?.Text ?? "";

            // Shared string
            if (cell.DataType?.Value == CellValues.SharedString &&
                int.TryParse(value, out var idx) &&
                sst != null)
            {
                var item = sst.Elements<SharedStringItem>().ElementAtOrDefault(idx);
                return Trunc(item?.InnerText ?? "");
            }

            return Trunc(value);
        }

        static string Trunc(string s)
        {
            s ??= "";
            s = s.Trim();
            return s.Length <= MaxCellChars ? s : s.Substring(0, MaxCellChars) + "…";
        }

        var results = new List<SheetTable>();

        var sheets = wbPart.Workbook.Sheets?.Elements<Sheet>().Take(MaxSheets).ToList()
                     ?? new List<Sheet>();

        foreach (var sheet in sheets)
        {
            var wsPart = wbPart.GetPartById(sheet.Id!) as WorksheetPart;
            var ws = wsPart?.Worksheet;
            if (ws == null)
            {
                results.Add(new SheetTable(sheet.Name ?? "Sheet", new List<string[]>()));
                continue;
            }

            var sheetData = ws.GetFirstChild<SheetData>();
            if (sheetData == null)
            {
                results.Add(new SheetTable(sheet.Name ?? "Sheet", new List<string[]>()));
                continue;
            }

            var rowsOut = new List<string[]>();

            // IMPORTANT: cap rows early
            foreach (var row in sheetData.Elements<Row>().Take(MaxRowsPerSheet))
            {
                // Only take first N cells (many sheets have tons of columns)
                var cells = row.Elements<Cell>().Take(MaxCellsPerRow).ToArray();

                var values = cells.Select(GetCellText).ToArray();

                // skip fully empty rows
                if (values.Length == 0 || values.All(string.IsNullOrWhiteSpace))
                    continue;

                rowsOut.Add(values);
            }

            results.Add(new SheetTable(sheet.Name ?? "Sheet", rowsOut));
        }

        return results;
    }

    // ---------------- XLS (bounded) ----------------

    private static List<SheetTable> ParseXlsBounded(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateReader(ms);

        var ds = reader.AsDataSet();

        var results = new List<SheetTable>();

        foreach (DataTable table in ds.Tables.Cast<DataTable>().Take(MaxSheets))
        {
            var rows = new List<string[]>();

            // cap rows
            var takeRows = Math.Min(table.Rows.Count, MaxRowsPerSheet);
            for (int r = 0; r < takeRows; r++)
            {
                var row = table.Rows[r];
                var itemArray = row.ItemArray;

                var takeCols = Math.Min(itemArray.Length, MaxCellsPerRow);

                var vals = new string[takeCols];
                for (int c = 0; c < takeCols; c++)
                {
                    var s = (itemArray[c]?.ToString() ?? "").Trim();
                    vals[c] = s.Length <= MaxCellChars ? s : s.Substring(0, MaxCellChars) + "…";
                }

                if (vals.All(string.IsNullOrWhiteSpace))
                    continue;

                rows.Add(vals);
            }

            results.Add(new SheetTable(table.TableName, rows));
        }

        return results;
    }

    // ---------------- CSV ----------------

    private static List<string[]> ParseCsv(byte[] bytes)
    {
        var csv = Encoding.UTF8.GetString(bytes);
        var rows = new List<string[]>();

        using var sr = new StringReader(csv);
        using var parser = new TextFieldParser(sr)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true
        };
        parser.SetDelimiters(",");

        int rowCount = 0;
        while (!parser.EndOfData && rowCount < MaxRowsPerSheet)
        {
            var fields = parser.ReadFields() ?? Array.Empty<string>();
            rows.Add(fields.Take(MaxCellsPerRow).Select(f =>
            {
                var s = (f ?? "").Trim();
                return s.Length <= MaxCellChars ? s : s.Substring(0, MaxCellChars) + "…";
            }).ToArray());
            rowCount++;
        }

        return rows;
    }

    // ---------------- Existing summarizer + chunker (keep yours or use these) ----------------

    public static string SummarizeTables(List<SheetTable> sheets, int maxDistinctPerColumn = 12, int sampleRows = 5)
    {
        // (keep your existing implementation; not repeated here)
        // If you want, paste yours and I’ll optimize it too.
        var sb = new StringBuilder();
        sb.AppendLine("TABLE SUMMARY (auto-generated):");
        sb.AppendLine();

        foreach (var sheet in sheets)
        {
            var rows = sheet.Rows.Where(r => r.Any(v => !string.IsNullOrWhiteSpace(v))).ToList();
            sb.AppendLine($"Sheet: {sheet.SheetName}");
            sb.AppendLine($"Rows: {rows.Count}");
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    public static List<string> SheetAwareChunk(
    List<SheetTable> sheets,
    int maxChars = 3500,
    int overlapRows = 2,
    int maxRowsPerSheet = 5000,
    int maxChunksTotal = 250,
    int maxLinesPerChunk = 200)
    {
        var chunks = new List<string>();

        foreach (var sheet in sheets)
        {
            if (chunks.Count >= maxChunksTotal) break;

            // remove empty rows
            var rows = sheet.Rows
                .Where(r => r != null && r.Any(v => !string.IsNullOrWhiteSpace(v)))
                .ToList();

            if (rows.Count == 0) continue;

            // Cap rows (CSV can be massive)
            if (rows.Count > maxRowsPerSheet)
                rows = rows.Take(maxRowsPerSheet).ToList();

            // Header heuristic: use first row as header
            var header = rows[0];
            var data = rows.Skip(1).ToList();

            var start = 0;
            while (start < data.Count && chunks.Count < maxChunksTotal)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# Sheet: {sheet.SheetName}");
                sb.AppendLine("Columns: " + string.Join(" | ", header.Select(x => (x ?? "").Trim())));
                sb.AppendLine();

                var end = start;
                var lines = 0;

                while (end < data.Count)
                {
                    var line = string.Join(" | ", data[end].Select(x => (x ?? "").Trim()));

                    // stop if we'd exceed caps
                    if (lines > 0 && (sb.Length + line.Length + 2 > maxChars))
                        break;

                    sb.AppendLine(line);
                    end++;
                    lines++;

                    if (lines >= maxLinesPerChunk)
                        break;
                }

                // Safety: if we couldn't add even one line, force progress by skipping one row
                if (end == start)
                    end = start + 1;

                chunks.Add(sb.ToString().Trim());

                // Next window: overlap but guarantee progress
                var nextStart = end - overlapRows;
                if (nextStart <= start) nextStart = start + 1;
                if (nextStart < 0) nextStart = 0;

                start = nextStart;
            }
        }

        return chunks;
    }
}