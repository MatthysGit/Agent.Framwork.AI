namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

using System.Data;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ExcelDataReader;
using Microsoft.VisualBasic.FileIO;
using UglyToad.PdfPig;

public class DocumentTextExtractor : IDocumentTextExtractor
{
    public Task<string> ExtractAsync(byte[] content, string contentType, string fileName)
    {
        var ext = (Path.GetExtension(fileName) ?? "").ToLowerInvariant();
        contentType ??= "";

        if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase) || ext == ".pdf")
            return Task.FromResult(ExtractPdf(content));

        if (contentType.Contains("word", StringComparison.OrdinalIgnoreCase) || ext == ".docx")
            return Task.FromResult(ExtractDocx(content));

        if (IsXlsx(contentType, ext))
            return Task.FromResult(ExtractXlsx(content));

        if (IsXls(contentType, ext))
            return Task.FromResult(ExtractXls(content));

        if (IsCsv(contentType, ext))
            return Task.FromResult(ExtractCsvAsText(content));

        // txt fallback
        return Task.FromResult(Encoding.UTF8.GetString(content));
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

    private string ExtractPdf(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = PdfDocument.Open(ms);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
            sb.AppendLine(page.Text);
        return sb.ToString();
    }

    private string ExtractDocx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(ms, false);
        return doc.MainDocumentPart?.Document?.Body?.InnerText ?? "";
    }


    private string ExtractXlsx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = SpreadsheetDocument.Open(ms, false);

        var wbPart = doc.WorkbookPart;
        if (wbPart?.Workbook == null)
            return "";

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

            // NOTE: This returns the raw stored value (dates are numbers unless styles are applied).
            // For search purposes, that's usually OK. If you want formatted dates, you need style parsing.
            return value;
        }

        static int ColumnNameToNumber(string columnName)
        {
            // A -> 1, B -> 2, Z -> 26, AA -> 27 ...
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

            // Extract letters from "C12" => "C", "AA4" => "AA"
            var letters = new string(cellRef
                .TakeWhile(char.IsLetter)
                .Select(ch => char.ToUpperInvariant(ch))
                .ToArray());

            if (letters.Length == 0) return -1;
            return ColumnNameToNumber(letters) - 1; // zero-based
        }

        var sb = new StringBuilder(64 * 1024);

        foreach (var sheet in wbPart.Workbook.Sheets?.Elements<Sheet>() ?? Enumerable.Empty<Sheet>())
        {
            var wsPart = wbPart.GetPartById(sheet.Id!) as WorksheetPart;
            var ws = wsPart?.Worksheet;
            if (ws == null) continue;

            sb.AppendLine($"=== Sheet: {sheet.Name} ===");

            var sheetData = ws.GetFirstChild<SheetData>();
            if (sheetData == null)
            {
                sb.AppendLine();
                continue;
            }

            // Build a dense grid per row (preserve column gaps)
            List<string[]> denseRows = new();
            int maxCols = 0;

            foreach (var row in sheetData.Elements<Row>())
            {
                // Map cells by column index
                var map = new SortedDictionary<int, string>(Comparer<int>.Default);
                foreach (var cell in row.Elements<Cell>())
                {
                    var col = GetColumnIndexFromCellRef(cell.CellReference?.Value);
                    if (col < 0) continue;
                    map[col] = (GetCellText(cell) ?? "").Trim();
                }

                if (map.Count == 0)
                    continue;

                maxCols = Math.Max(maxCols, map.Keys.Max() + 1);

                // We'll densify later once we know maxCols
                // For now, store sparse as temp: densify with maxCols
                denseRows.Add(new string[] { "__SPARSE__" }); // placeholder marker
                denseRows[^1] = BuildDense(map, maxCols);     // densify with current maxCols
            }

            // If maxCols grew later, re-densify earlier rows
            // (simple approach: re-scan once more)
            if (maxCols == 0 || denseRows.Count == 0)
            {
                sb.AppendLine();
                continue;
            }

            // Rebuild rows with final maxCols to ensure alignment
            denseRows.Clear();
            foreach (var row in sheetData.Elements<Row>())
            {
                var map = new SortedDictionary<int, string>(Comparer<int>.Default);
                foreach (var cell in row.Elements<Cell>())
                {
                    var col = GetColumnIndexFromCellRef(cell.CellReference?.Value);
                    if (col < 0) continue;
                    map[col] = (GetCellText(cell) ?? "").Trim();
                }
                if (map.Count == 0) continue;
                denseRows.Add(BuildDense(map, maxCols));
            }

            // Find header row (first non-empty row)
            int headerRowIndex = -1;
            for (int i = 0; i < denseRows.Count; i++)
            {
                if (denseRows[i].Any(v => !string.IsNullOrWhiteSpace(v)))
                {
                    headerRowIndex = i;
                    break;
                }
            }

            if (headerRowIndex < 0)
            {
                sb.AppendLine();
                continue;
            }

            var headers = denseRows[headerRowIndex]
                .Select((h, idx) => string.IsNullOrWhiteSpace(h) ? $"Column{idx + 1}" : h.Trim())
                .ToArray();

            // Emit rows below header as key/value records
            for (int i = headerRowIndex + 1; i < denseRows.Count; i++)
            {
                var rowVals = denseRows[i];
                if (rowVals.All(string.IsNullOrWhiteSpace))
                    continue;

                sb.Append("Row ");
                sb.Append(i + 1); // 1-based within extracted rows (not Excel row number, but stable)
                sb.Append(": ");

                bool any = false;
                for (int c = 0; c < headers.Length; c++)
                {
                    var v = c < rowVals.Length ? rowVals[c]?.Trim() : "";
                    if (!string.IsNullOrWhiteSpace(v)) any = true;

                    sb.Append(headers[c]);
                    sb.Append(": ");
                    sb.Append(string.IsNullOrWhiteSpace(v) ? "∅" : v);
                    sb.Append(" | ");
                }

                if (any)
                    sb.AppendLine();
            }

            sb.AppendLine();
        }

        return sb.ToString();

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
    }

    private string ExtractXls(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateReader(ms);
        var ds = reader.AsDataSet();

        var sb = new StringBuilder(64 * 1024);

        foreach (DataTable table in ds.Tables)
        {
            sb.AppendLine($"=== Sheet: {table.TableName} ===");

            if (table.Rows.Count == 0 || table.Columns.Count == 0)
            {
                sb.AppendLine();
                continue;
            }

            // Header row = first row
            var headers = Enumerable.Range(0, table.Columns.Count)
                .Select(i =>
                {
                    var h = table.Rows[0][i]?.ToString()?.Trim();
                    return string.IsNullOrWhiteSpace(h) ? $"Column{i + 1}" : h!;
                })
                .ToArray();

            for (int r = 1; r < table.Rows.Count; r++)
            {
                var row = table.Rows[r];
                var values = row.ItemArray.Select(v => v?.ToString()?.Trim() ?? "").ToArray();
                if (values.All(string.IsNullOrWhiteSpace))
                    continue;

                sb.Append($"Row {r + 1}: ");

                bool any = false;
                for (int c = 0; c < headers.Length; c++)
                {
                    var v = c < values.Length ? values[c] : "";
                    if (!string.IsNullOrWhiteSpace(v)) any = true;

                    sb.Append(headers[c]);
                    sb.Append(": ");
                    sb.Append(string.IsNullOrWhiteSpace(v) ? "∅" : v);
                    sb.Append(" | ");
                }

                if (any)
                    sb.AppendLine();
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string ExtractCsvAsText(byte[] bytes)
    {
        var csv = Encoding.UTF8.GetString(bytes);
        var rows = CsvParser.Parse(csv);

        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine("=== Sheet: CSV ===");

        if (rows.Count == 0)
            return sb.ToString();

        // Header from first non-empty row
        int headerIndex = rows.FindIndex(r => r.Any(v => !string.IsNullOrWhiteSpace(v)));
        if (headerIndex < 0)
            return sb.ToString();

        var headers = rows[headerIndex]
            .Select((h, idx) => string.IsNullOrWhiteSpace(h) ? $"Column{idx + 1}" : h.Trim())
            .ToArray();

        for (int i = headerIndex + 1; i < rows.Count; i++)
        {
            var row = rows[i].Select(c => (c ?? "").Trim()).ToArray();
            if (row.All(string.IsNullOrWhiteSpace))
                continue;

            sb.Append($"Row {i + 1}: ");

            bool any = false;
            for (int c = 0; c < headers.Length; c++)
            {
                var v = c < row.Length ? row[c] : "";
                if (!string.IsNullOrWhiteSpace(v)) any = true;

                sb.Append(headers[c]);
                sb.Append(": ");
                sb.Append(string.IsNullOrWhiteSpace(v) ? "∅" : v);
                sb.Append(" | ");
            }

            if (any)
                sb.AppendLine();
        }

        return sb.ToString();
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