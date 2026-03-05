using SkiaSharp;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.charts;

/// <summary>
/// Gauge chart (semi-circle) for a single value in [min,max].
/// </summary>
public sealed class GaugeChartRenderer
{
    public byte[] RenderPng(
        string title,
        double value,
        double min = 0,
        double max = 100,
        int width = 450,
        int height = 250)
    {
        if (max <= min) max = min + 1;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        var padding = 30f;
        var (titlePaint, labelPaint, smallPaint, axisPaint, gridPaint, valuePaint) = ChartRenderHelpers.CreateDefaultPaints();
        ChartRenderHelpers.DrawTitle(canvas, title, width, padding, titlePaint);

        var cx = width / 2f;
        var cy = height * 0.78f;
        var radius = MathF.Min(width, height) * 0.36f;

        var clamped = Math.Min(max, Math.Max(min, value));
        var t = (float)((clamped - min) / (max - min)); // 0..1
        var angle = 180f + (t * 180f); // 180..360 degrees

        var arcRect = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);

        // background arc
        using var bgArc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 26, Color = new SKColor(230, 230, 230) };
        canvas.DrawArc(arcRect, 180, 180, false, bgArc);

        // value arc
        using var fgArc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 26, Color = new SKColor(60, 110, 200) };
        canvas.DrawArc(arcRect, 180, 180 * t, false, fgArc);

        // ticks
        using var tickPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(80, 80, 80) };
        var tickCount = 5;
        for (int i = 0; i <= tickCount; i++)
        {
            var tt = i / (float)tickCount;
            var a = (180f + 180f * tt) * (MathF.PI / 180f);

            var r0 = radius - 20;
            var r1 = radius - 5;

            var x0 = cx + r0 * MathF.Cos(a);
            var y0 = cy + r0 * MathF.Sin(a);
            var x1 = cx + r1 * MathF.Cos(a);
            var y1 = cy + r1 * MathF.Sin(a);

            canvas.DrawLine(x0, y0, x1, y1, tickPaint);

            var tickVal = min + (max - min) * tt;
            var txt = ChartRenderHelpers.FormatTick(tickVal);
            var tw = smallPaint.MeasureText(txt);

            var rt = radius - 40;
            var xt = cx + rt * MathF.Cos(a);
            var yt = cy + rt * MathF.Sin(a);

            canvas.DrawText(txt, xt - tw / 2f, yt + 5, smallPaint);
        }

        // needle
        var needleAngle = angle * (MathF.PI / 180f);
        var nx = cx + (radius - 35) * MathF.Cos(needleAngle);
        var ny = cy + (radius - 35) * MathF.Sin(needleAngle);

        using var needlePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, Color = SKColors.Black };
        canvas.DrawLine(cx, cy, nx, ny, needlePaint);

        // hub
        using var hub = new SKPaint { IsAntialias = true, Color = SKColors.Black, Style = SKPaintStyle.Fill };
        canvas.DrawCircle(cx, cy, 7, hub);

        // value text
        var valueText = $"{ChartRenderHelpers.FormatValue(clamped)}";
        var vb = new SKRect();
        valuePaint.MeasureText(valueText, ref vb);
        canvas.DrawText(valueText, cx - vb.Width / 2f, height * 0.62f, valuePaint);

        titlePaint.Dispose(); labelPaint.Dispose(); smallPaint.Dispose(); axisPaint.Dispose(); gridPaint.Dispose(); valuePaint.Dispose();
        return ChartRenderHelpers.EncodePng(surface);
    }
}
