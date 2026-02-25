using SkiaSharp;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.charts;

/// <summary>
/// Column chart (vertical bars). If you want horizontal bars, use a dedicated Bar (horizontal) renderer.
/// </summary>
public sealed class ColumnChartRenderer
{
    public byte[] RenderPng(
        string title,
        string xAxisLabel,
        string yAxisLabel,
        IReadOnlyList<string> labels,
        IReadOnlyList<double> values,
        int width = 1200,
        int height = 700)
    {
        if (labels.Count != values.Count) throw new ArgumentException("labels and values must have the same length.");
        if (labels.Count == 0) throw new ArgumentException("No data to render.");

        var padding = 30f;
        var titleHeight = 70f;
        var leftMargin = 110f;
        var rightMargin = 30f;
        var bottomMargin = 230f;
        var topMargin = padding + titleHeight;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        var (titlePaint, labelPaint, smallPaint, axisPaint, gridPaint, valuePaint) = ChartRenderHelpers.CreateDefaultPaints();
        using var barPaint = new SKPaint { IsAntialias = true, Color = new SKColor(60, 110, 200), Style = SKPaintStyle.Fill };

        ChartRenderHelpers.DrawTitle(canvas, title, width, padding, titlePaint);

        var plotLeft = leftMargin;
        var plotTop = topMargin;
        var plotRight = width - rightMargin;
        var plotBottom = height - bottomMargin;

        var plotW = plotRight - plotLeft;
        var plotH = plotBottom - plotTop;

        var maxVal = Math.Max(0, values.Max());
        var yMax = ChartRenderHelpers.NiceCeiling(maxVal);
        if (yMax <= 0) yMax = 1;

        ChartRenderHelpers.DrawAxesAndYGrid(canvas, plotLeft, plotTop, plotRight, plotBottom, plotH, yMax, 5, axisPaint, gridPaint, smallPaint);

        var n = labels.Count;
        var slotW = plotW / n;
        var barW = MathF.Min(60f, slotW * 0.65f);
        var rotateLabels = n > 8 || slotW < 90f;
        var labelRotation = -40f;
        var labelYOffset = rotateLabels ? 45f : 22f;

        for (int i = 0; i < n; i++)
        {
            var v = Math.Max(0, values[i]);
            var barH = (float)(v / yMax) * plotH;

            var cx = plotLeft + (i + 0.5f) * slotW;
            var x0 = cx - (barW / 2f);
            var y0 = plotBottom - barH;

            canvas.DrawRect(new SKRect(x0, y0, x0 + barW, plotBottom), barPaint);

            var valueText = ChartRenderHelpers.FormatValue(values[i]);
            var vtW = valuePaint.MeasureText(valueText);
            canvas.DrawText(valueText, cx - vtW / 2f, y0 - 6, valuePaint);

            var label = ChartRenderHelpers.TrimLabel(labels[i] ?? string.Empty, 24);

            if (!rotateLabels)
            {
                var lw = smallPaint.MeasureText(label);
                canvas.DrawText(label, cx - lw / 2f, plotBottom + labelYOffset, smallPaint);
            }
            else
            {
                var bounds = new SKRect();
                smallPaint.MeasureText(label, ref bounds);

                canvas.Save();
                canvas.Translate(cx, plotBottom + labelYOffset);
                canvas.RotateDegrees(labelRotation);
                canvas.DrawText(label, -bounds.MidX, -bounds.Top, smallPaint);
                canvas.Restore();
            }
        }

        if (!string.IsNullOrWhiteSpace(xAxisLabel))
        {
            var xw = labelPaint.MeasureText(xAxisLabel);
            canvas.DrawText(xAxisLabel, plotLeft + (plotW - xw) / 2f, height - 40, labelPaint);
        }

        if (!string.IsNullOrWhiteSpace(yAxisLabel))
        {
            canvas.Save();
            canvas.Translate(35, plotTop + plotH / 2f);
            canvas.RotateDegrees(-90);
            var yw = labelPaint.MeasureText(yAxisLabel);
            canvas.DrawText(yAxisLabel, -yw / 2f, 0, labelPaint);
            canvas.Restore();
        }

        titlePaint.Dispose(); labelPaint.Dispose(); smallPaint.Dispose(); axisPaint.Dispose(); gridPaint.Dispose(); valuePaint.Dispose();
        return ChartRenderHelpers.EncodePng(surface);
    }
}