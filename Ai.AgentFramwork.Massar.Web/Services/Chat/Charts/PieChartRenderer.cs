using SkiaSharp;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.charts;

public sealed class PieChartRenderer
{
    public byte[] RenderPng(string title, string[] labels, double[] values, int width = 900, int height = 600)
    {
        if (labels is null || values is null || labels.Length == 0 || labels.Length != values.Length)
            throw new ArgumentException("labels and values must be the same non-zero length.");

        width = Math.Clamp(width, 400, 2000);
        height = Math.Clamp(height, 300, 2000);

        var safe = values.Select(v => double.IsFinite(v) && v > 0 ? v : 0).ToArray();
        var total = safe.Sum();
        if (total <= 0)
            throw new ArgumentException("values must include at least one positive number.");

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        var margin = 40f;
        var pieSize = Math.Min(width, height) - (int)(margin * 2);
        var pieRect = SKRect.Create(margin, margin + 55, pieSize, pieSize);

        using (var titlePaint = new SKPaint { IsAntialias = true, Color = SKColors.Black, TextSize = 28, Typeface = SKTypeface.Default })
        {
            canvas.DrawText(string.IsNullOrWhiteSpace(title) ? "Pie chart" : title, margin, margin + 30, titlePaint);
        }

        var startAngle = -90f;
        for (var i = 0; i < safe.Length; i++)
        {
            var sweep = (float)(safe[i] / total * 360.0);
            if (sweep <= 0) continue;

            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ColorForIndex(i) };
            using var path = new SKPath();
            path.MoveTo(pieRect.MidX, pieRect.MidY);
            path.ArcTo(pieRect, startAngle, sweep, false);
            path.Close();
            canvas.DrawPath(path, paint);
            startAngle += sweep;
        }

        var legendX = pieRect.Right + 40;
        var legendY = pieRect.Top;

        using var legendText = new SKPaint { IsAntialias = true, Color = SKColors.Black, TextSize = 18 };
        using var legendBox = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        for (var i = 0; i < labels.Length; i++)
        {
            var pct = safe[i] / total * 100.0;
            var line = $"{labels[i]} — {pct:0.#}%";

            legendBox.Color = ColorForIndex(i);
            canvas.DrawRect(SKRect.Create(legendX, legendY + (i * 28), 18, 18), legendBox);
            canvas.DrawText(line, legendX + 26, legendY + 15 + (i * 28), legendText);
        }

        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 95);
        return data.ToArray();
    }

    private static SKColor ColorForIndex(int i)
    {
        SKColor[] palette =
        [
            new SKColor(0x4E, 0x79, 0xA7),
            new SKColor(0xF2, 0x8E, 0x2B),
            new SKColor(0xE1, 0x57, 0x59),
            new SKColor(0x76, 0xB7, 0xB2),
            new SKColor(0x59, 0xA1, 0x4F),
            new SKColor(0xED, 0xC9, 0x49),
            new SKColor(0xAF, 0x7A, 0xC5),
            new SKColor(0xFF, 0x9D, 0xA7)
        ];
        return palette[i % palette.Length];
    }
}
