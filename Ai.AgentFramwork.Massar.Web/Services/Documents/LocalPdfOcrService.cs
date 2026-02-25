using System.Drawing;
using System.Text;
using Microsoft.Extensions.Logging;
using Tesseract;
using PdfiumViewer;

using PdfiumDoc = PdfiumViewer.PdfDocument;
using PdfiumFlags = PdfiumViewer.PdfRenderFlags;


namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public interface IPdfOcrService
{
    Task<string> ExtractTextAsync(byte[] pdfBytes, CancellationToken ct = default);
}


public sealed class LocalPdfOcrService : IPdfOcrService
{
    private readonly string _tessDataPath;
    private readonly string _language;
    private readonly int _dpi;
    private readonly int _maxPages;
    private readonly ILogger<LocalPdfOcrService> _logger;

    public LocalPdfOcrService(
        string tessDataPath,
        ILogger<LocalPdfOcrService> logger,
        string language = "eng",
        int dpi = 300,
        int maxPages = 200)
    {
        _tessDataPath = tessDataPath;
        _language = language;
        _dpi = dpi;
        _maxPages = maxPages;
        _logger = logger;
    }

    public Task<string> ExtractTextAsync(byte[] bytes, CancellationToken ct = default)
        => Task.Run(() => ExtractTextSync(bytes, ct), ct);

    private string ExtractTextSync(byte[] bytes, CancellationToken ct)
    {
        if (bytes is null || bytes.Length == 0)
            return string.Empty;

        if (!Directory.Exists(_tessDataPath))
            throw new InvalidOperationException($"tessdata folder not found: '{_tessDataPath}'");

        var trainedData = Path.Combine(_tessDataPath, $"{_language}.traineddata");
        if (!File.Exists(trainedData))
            throw new InvalidOperationException($"traineddata file not found: '{trainedData}'");

        using var engine = new TesseractEngine(_tessDataPath, _language, EngineMode.LstmOnly);
        engine.DefaultPageSegMode = PageSegMode.Auto;

        if (IsPdf(bytes))
        {
            _logger.LogWarning("OCR: PDF detected. Rendering pages then OCR. bytes={Len}, dpi={Dpi}", bytes.Length, _dpi);

            using var ms = new MemoryStream(bytes);
            using var pdf = PdfiumDoc.Load(ms);

            var pages = Math.Min(pdf.PageCount, _maxPages);
            var sb = new StringBuilder();

            for (int i = 0; i < pages; i++)
            {
                ct.ThrowIfCancellationRequested();

                var size = pdf.PageSizes[i];
                var widthPx = (int)Math.Ceiling(size.Width / 72f * _dpi);
                var heightPx = (int)Math.Ceiling(size.Height / 72f * _dpi);

                widthPx = Math.Min(widthPx, 5000);
                heightPx = Math.Min(heightPx, 5000);

                using var bmp = (Bitmap)pdf.Render(
                    page: i,
                    width: widthPx,
                    height: heightPx,
                    dpiX: _dpi,
                    dpiY: _dpi,
                    flags: PdfiumFlags.Annotations);

                var pngBytes = BitmapToPngBytes(bmp);

                using var pix = Pix.LoadFromMemory(pngBytes);
                using var ocrPage = engine.Process(pix);

                var text = (ocrPage.GetText() ?? string.Empty).Trim();
                _logger.LogInformation("OCR page {Page}/{Pages}: chars={Chars}", i + 1, pages, text.Length);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text);
                    sb.AppendLine();
                }
            }

            return sb.ToString().Trim();
        }

        // If it's already an image
        using var pixImg = Pix.LoadFromMemory(bytes);
        using var pageImg = engine.Process(pixImg);
        return (pageImg.GetText() ?? string.Empty).Trim();
    }

    private static byte[] BitmapToPngBytes(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); // ✅ fully qualified
        return ms.ToArray();
    }

    private static bool IsPdf(byte[] bytes)
        => bytes.Length >= 4
           && bytes[0] == (byte)'%'
           && bytes[1] == (byte)'P'
           && bytes[2] == (byte)'D'
           && bytes[3] == (byte)'F';
}
