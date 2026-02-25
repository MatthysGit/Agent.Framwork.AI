using SkiaSharp;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.charts;

/// <summary>Donut chart (pie with a hole). Supports single-series categorical data.</summary>
public sealed class DonutChartRenderer
{
    public byte[] RenderPng(
        string title,
        IReadOnlyList<string> labels,
        IReadOnlyList<double> values,
        int width = 900,
        int height = 600,
        float innerRadiusRatio = 0.55f)
    {
        if (labels.Count != values.Count) throw new ArgumentException("labels and values must have the same length.");
        if (labels.Count == 0) throw new ArgumentException("No data to render.");
        if (innerRadiusRatio <= 0 || innerRadiusRatio >= 0.9f) innerRadiusRatio = 0.55f;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        var padding = 30f;
        var (titlePaint, labelPaint, smallPaint, axisPaint, gridPaint, valuePaint) = ChartRenderHelpers.CreateDefaultPaints();
        ChartRenderHelpers.DrawTitle(canvas, title, width, padding, titlePaint);

        var cx = width * 0.38f;
        var cy = height * 0.55f;
        var radius = MathF.Min(width, height) * 0.28f;
        var innerRadius = radius * innerRadiusRatio;

        var total = values.Sum(v => Math.Max(0, v));
        if (total <= 0) total = 1;

        // simple palette (deterministic)
        SKColor ColorFor(int i)
        {
            // distribute hues (cheap but effective)
            var hue = (i * 360f / Math.Max(1, labels.Count)) % 360f;
            return SKColor.FromHsl(hue, 60, 55);
        }

        var startAngle = -90f;
        using var slicePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var ringOutline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(240, 240, 240), StrokeWidth = 2 };

        var rect = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);

        for (int i = 0; i < labels.Count; i++)
        {
            var v = Math.Max(0, values[i]);
            var sweep = (float)(v / total) * 360f;
            if (sweep <= 0.01f) continue;

            slicePaint.Color = ColorFor(i);
            canvas.DrawArc(rect, startAngle, sweep, true, slicePaint);
            startAngle += sweep;
        }

        // punch hole
        using var holePaint = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
        canvas.DrawCircle(cx, cy, innerRadius, holePaint);
        canvas.DrawCircle(cx, cy, radius, ringOutline);

        // legend
        var legendX = width * 0.70f;
        var legendY = height * 0.20f;
        var rowH = 22f;

        for (int i = 0; i < labels.Count; i++)
        {
            var v = Math.Max(0, values[i]);
            var pct = v / total * 100.0;
            var text = $"{ChartRenderHelpers.TrimLabel(labels[i] ?? "", 20)}  {ChartRenderHelpers.FormatValue(v)} ({pct:0.#}%)";

            using var swatch = new SKPaint { IsAntialias = true, Color = ColorFor(i), Style = SKPaintStyle.Fill };
            canvas.DrawRect(new SKRect(legendX, legendY + i * rowH - 12, legendX + 14, legendY + i * rowH + 2), swatch);
            canvas.DrawText(text, legendX + 20, legendY + i * rowH, smallPaint);
        }

        titlePaint.Dispose(); labelPaint.Dispose(); smallPaint.Dispose(); axisPaint.Dispose(); gridPaint.Dispose(); valuePaint.Dispose();
        return ChartRenderHelpers.EncodePng(surface);
    }
}