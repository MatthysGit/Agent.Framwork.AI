using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ExcelDataReader;
using Microsoft.VisualBasic.FileIO;
using System.Data;
using System.Text;
using Ai.AgentFramwork.Massar.Web.Models;

namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public sealed class SpreadsheetTableExtractor : ISpreadsheetTableExtractor
{
    public Task<TabularData> ExtractTableAsync(
        byte[] content,
        string contentType,
        string fileName,
        string? sheetName = null,
        CancellationToken ct = default)
    {
        var ext = (Path.GetExtension(fileName) ?? "").ToLowerInvariant();
        contentType ??= "";

        if (IsXlsx(contentType, ext))
            return Task.FromResult(ExtractXlsxTable(content, sheetName));

        if (IsXls(contentType, ext))
            return Task.FromResult(ExtractXlsTable(content, sheetName));

        if (IsCsv(contentType, ext))
            return Task.FromResult(ExtractCsvTable(content));

        return Task.FromResult(new TabularData("Unknown", Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>()));
    }

    private static bool IsXlsx(string contentType, string ext)
        => string.Equals(contentType, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase)
           || ext == ".xlsx";

    private static bool IsXls(string contentType, string ext)
        => string.Equals(contentType, "application/vnd.ms-excel", StringComparison.OrdinalIgnoreCase)
           || ext == ".xls";

    private static bool IsCsv(string contentType, string ext)
        => string.Equals(contentType, "text/csv", StringComparison.OrdinalIgnoreCase)
           || string.Equals(contentType, "application/csv", StringComparison.OrdinalIgnoreCase)
           || ext == ".csv";

    private static TabularData ExtractXlsxTable(byte[] bytes, string? desiredSheetName)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = SpreadsheetDocument.Open(ms, false);

        var wbPart = doc.WorkbookPart;
        if (wbPart?.Workbook?.Sheets == null)
            return new TabularData("Workbook", Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>());

        var sst = wbPart.SharedStringTablePart?.SharedStringTable;

        string GetCellText(Cell? cell)
        {
            if (cell == null) return "";

            var value = cell.CellValue?.Text ?? "";

            if (cell.DataType?.Value == CellValues.SharedString &&
                int.TryParse(value, out var idx) &&
                sst != null)
            {
                var item = sst.Elements<SharedStringItem>().ElementAtOrDefault(idx);
                return item?.InnerText ?? "";
            }

            if (cell.DataType?.Value == CellValues.InlineString)
                return cell.InnerText ?? "";

            return value ?? "";
        }

        static int ColumnNameToNumber(string columnName)
        {
            int sum = 0;
            foreach (var ch in columnName)
            {
                if (ch < 'A' || ch > 'Z') break;
                sum = (sum * 26) + (ch - 'A' + 1);
            }
            return sum;
        }

        static int GetColumnIndexFromCellRef(string? cellRef)
        {
            if (string.IsNullOrWhiteSpace(cellRef))
                return -1;

            var letters = new string(cellRef
                .TakeWhile(char.IsLetter)
                .Select(ch => char.ToUpperInvariant(ch))
                .ToArray());

            if (letters.Length == 0) return -1;
            return ColumnNameToNumber(letters) - 1;
        }

        static string[] BuildDense(SortedDictionary<int, string> map, int maxCols)
        {
            var arr = new string[maxCols];
            foreach (var kvp in map)
            {
                if (kvp.Key >= 0 && kvp.Key < maxCols)
                    arr[kvp.Key] = kvp.Value ?? "";
            }
            for (int i = 0; i < arr.Length; i++)
                arr[i] ??= "";
            return arr;
        }

        var candidates = new List<(string SheetName, List<string[]> DenseRows)>();

        foreach (var sheet in wbPart.Workbook.Sheets.Elements<Sheet>())
        {
            if (!string.IsNullOrWhiteSpace(desiredSheetName) &&
                !string.Equals(sheet.Name?.Value, desiredSheetName, StringComparison.OrdinalIgnoreCase))
                continue;

            var wsPart = wbPart.GetPartById(sheet.Id!) as WorksheetPart;
            var ws = wsPart?.Worksheet;
            var sheetData = ws?.GetFirstChild<SheetData>();
            if (sheetData == null) continue;

            int maxCols = 0;
            var sparse = new List<SortedDictionary<int, string>>();

            foreach (var row in sheetData.Elements<Row>())
            {
                var map = new SortedDictionary<int, string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var col = GetColumnIndexFromCellRef(cell.CellReference?.Value);
                    if (col < 0) continue;
                    var txt = (GetCellText(cell) ?? "").Trim();
                    map[col] = txt;
                }

                if (map.Count == 0) continue;
                maxCols = Math.Max(maxCols, map.Keys.Max() + 1);
                sparse.Add(map);
            }

            if (maxCols == 0 || sparse.Count == 0) continue;

            var denseRows = sparse.Select(m => BuildDense(m, maxCols)).ToList();
            candidates.Add((sheet.Name?.Value ?? "Sheet", denseRows));
        }

        if (candidates.Count == 0)
            return new TabularData("Workbook", Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>());

        var best = candidates
            .OrderByDescending(s => s.DenseRows.Count(r => r.Any(v => !string.IsNullOrWhiteSpace(v))))
            .First();

        return DenseRowsToTabular(best.SheetName, best.DenseRows);
    }

    private static TabularData DenseRowsToTabular(string sheetName, List<string[]> denseRows)
    {
        int headerRowIndex = denseRows.FindIndex(r => r.Any(v => !string.IsNullOrWhiteSpace(v)));
        if (headerRowIndex < 0)
            return new TabularData(sheetName, Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>());

        var headers = denseRows[headerRowIndex]
            .Select((h, idx) => string.IsNullOrWhiteSpace(h) ? $"Column{idx + 1}" : h.Trim())
            .ToArray();

        var rows = new List<IReadOnlyList<object?>>();

        for (int i = headerRowIndex + 1; i < denseRows.Count; i++)
        {
            var r = denseRows[i];
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            rows.Add(r.Select(v => (object?)v).ToList());
        }

        return new TabularData(sheetName, headers, rows);
    }

    private static TabularData ExtractXlsTable(byte[] bytes, string? desiredSheetName)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateReader(ms);
        var ds = reader.AsDataSet();

        if (ds.Tables.Count == 0)
            return new TabularData("Workbook", Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>());

        var tables = ds.Tables.Cast<DataTable>().ToList();

        if (!string.IsNullOrWhiteSpace(desiredSheetName))
        {
            var match = tables.FirstOrDefault(t => string.Equals(t.TableName, desiredSheetName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return DataTableToTabular(match);
        }

        var best = tables
            .OrderByDescending(t => t.Rows.Cast<DataRow>().Count(r => r.ItemArray.Any(v => !string.IsNullOrWhiteSpace(v?.ToString()))))
            .First();

        return DataTableToTabular(best);
    }

    private static TabularData DataTableToTabular(DataTable table)
    {
        if (table.Rows.Count == 0 || table.Columns.Count == 0)
            return new TabularData(table.TableName, Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>());

        var headers = Enumerable.Range(0, table.Columns.Count)
            .Select(i =>
            {
                var h = table.Rows[0][i]?.ToString()?.Trim();
                return string.IsNullOrWhiteSpace(h) ? $"Column{i + 1}" : h!;
            })
            .ToArray();

        var rows = new List<IReadOnlyList<object?>>();
        for (int r = 1; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var vals = row.ItemArray.Select(v => (object?)(v?.ToString()?.Trim() ?? "")).ToList();
            if (vals.All(v => string.IsNullOrWhiteSpace(v?.ToString()))) continue;
            rows.Add(vals);
        }

        return new TabularData(table.TableName, headers, rows);
    }

    private static TabularData ExtractCsvTable(byte[] bytes)
    {
        var csv = Encoding.UTF8.GetString(bytes);
        var rows = CsvParser.Parse(csv);

        if (rows.Count == 0)
            return new TabularData("CSV", Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>());

        int headerIndex = rows.FindIndex(r => r.Any(v => !string.IsNullOrWhiteSpace(v)));
        if (headerIndex < 0)
            return new TabularData("CSV", Array.Empty<string>(), Array.Empty<IReadOnlyList<object?>>());

        var headers = rows[headerIndex]
            .Select((h, idx) => string.IsNullOrWhiteSpace(h) ? $"Column{idx + 1}" : h.Trim())
            .ToArray();

        var data = new List<IReadOnlyList<object?>>();
        for (int i = headerIndex + 1; i < rows.Count; i++)
        {
            var row = rows[i].Select(c => (object?)((c ?? "").Trim())).ToList();
            if (row.All(v => string.IsNullOrWhiteSpace(v?.ToString()))) continue;
            data.Add(row);
        }

        return new TabularData("CSV", headers, data);
    }

    private static class CsvParser
    {
        public static List<string[]> Parse(string csvText)
        {
            var rows = new List<string[]>();
            using var sr = new StringReader(csvText);
            using var parser = new TextFieldParser(sr)
            {
                TextFieldType = FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true
            };
            parser.SetDelimiters(",");

            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields() ?? Array.Empty<string>();
                rows.Add(fields);
            }

            return rows;
        }
    }
}
