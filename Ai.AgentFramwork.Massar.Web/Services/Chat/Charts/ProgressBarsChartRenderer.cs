using SkiaSharp;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.charts;

/// <summary>
/// Progress bars chart: multiple labeled progress bars.
/// Each value is compared to maxValue (or 100 by default).
/// </summary>
public sealed class ProgressBarsChartRenderer
{
    public byte[] RenderPng(
        string title,
        IReadOnlyList<string> labels,
        IReadOnlyList<double> values,
        double maxValue = 100,
        int width = 1000,
        int height = 600)
    {
        if (labels.Count != values.Count) throw new ArgumentException("labels and values must have the same length.");
        if (labels.Count == 0) throw new ArgumentException("No data to render.");
        if (maxValue <= 0) maxValue = 100;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        var padding = 30f;
        var (titlePaint, labelPaint, smallPaint, axisPaint, gridPaint, valuePaint) = ChartRenderHelpers.CreateDefaultPaints();
        ChartRenderHelpers.DrawTitle(canvas, title, width, padding, titlePaint);

        var top = padding + 70f;
        var left = 60f;
        var right = width - 60f;
        var barAreaW = right - left;

        var rowH = MathF.Min(50f, (height - top - 60f) / Math.Max(1, labels.Count));
        var barH = MathF.Min(18f, rowH * 0.45f);

        using var trackPaint = new SKPaint { IsAntialias = true, Color = new SKColor(235, 235, 235), Style = SKPaintStyle.Fill };
        using var fillPaint = new SKPaint { IsAntialias = true, Color = new SKColor(60, 110, 200), Style = SKPaintStyle.Fill };

        for (int i = 0; i < labels.Count; i++)
        {
            var y = top + i * rowH;

            var label = ChartRenderHelpers.TrimLabel(labels[i] ?? "", 28);
            canvas.DrawText(label, left, y, labelPaint);

            var barY = y + 10;
            var barX = left;
            var barW = barAreaW;

            var track = new SKRoundRect(new SKRect(barX, barY, barX + barW, barY + barH), 8, 8);
            canvas.DrawRoundRect(track, trackPaint);

            var v = Math.Max(0, values[i]);
            var pct = Math.Min(1.0, v / maxValue);
            var fillW = (float)(barW * pct);

            var fill = new SKRoundRect(new SKRect(barX, barY, barX + fillW, barY + barH), 8, 8);
            canvas.DrawRoundRect(fill, fillPaint);

            var txt = $"{ChartRenderHelpers.FormatValue(v)} / {ChartRenderHelpers.FormatValue(maxValue)}  ({pct * 100:0.#}%)";
            var tw = smallPaint.MeasureText(txt);
            canvas.DrawText(txt, barX + barW - tw, barY + barH + 18, smallPaint);
        }

        titlePaint.Dispose(); labelPaint.Dispose(); smallPaint.Dispose(); axisPaint.Dispose(); gridPaint.Dispose(); valuePaint.Dispose();
        return ChartRenderHelpers.EncodePng(surface);
    }
}