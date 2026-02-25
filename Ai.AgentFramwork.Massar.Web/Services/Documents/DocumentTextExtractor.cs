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

            return value;
        }

        var sb = new StringBuilder();

        foreach (var sheet in wbPart.Workbook.Sheets?.Elements<Sheet>() ?? Enumerable.Empty<Sheet>())
        {
            var wsPart = wbPart.GetPartById(sheet.Id!) as WorksheetPart;
            var ws = wsPart?.Worksheet;
            if (ws == null) continue;

            sb.AppendLine($"# Sheet: {sheet.Name}");

            var sheetData = ws.GetFirstChild<SheetData>();
            if (sheetData == null)
            {
                sb.AppendLine();
                continue;
            }

            foreach (var row in sheetData.Elements<Row>())
            {
                var values = row.Elements<Cell>()
                    .Select(GetCellText)
                    .Select(v => (v ?? "").Trim())
                    .ToArray();

                if (values.Length == 0 || values.All(string.IsNullOrWhiteSpace))
                    continue;

                sb.AppendLine(string.Join("\t", values));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string ExtractXls(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateReader(ms);
        var ds = reader.AsDataSet();

        var sb = new StringBuilder();

        foreach (DataTable table in ds.Tables)
        {
            sb.AppendLine($"# Sheet: {table.TableName}");

            foreach (DataRow row in table.Rows)
            {
                var values = row.ItemArray
                    .Select(v => v?.ToString()?.Trim() ?? "")
                    .ToArray();

                if (values.All(string.IsNullOrWhiteSpace))
                    continue;

                sb.AppendLine(string.Join("\t", values));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string ExtractCsvAsText(byte[] bytes)
    {
        // Basic “safe” UTF8; if you expect other encodings, you can try detect or allow override.
        var csv = Encoding.UTF8.GetString(bytes);

        // Turn into TSV-ish text for better chunking/embedding
        var rows = CsvParser.Parse(csv);
        var sb = new StringBuilder();
        sb.AppendLine("# Sheet: CSV");

        foreach (var row in rows)
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            sb.AppendLine(string.Join("\t", row.Select(c => (c ?? "").Trim())));
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