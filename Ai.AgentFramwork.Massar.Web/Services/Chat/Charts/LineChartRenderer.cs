using SkiaSharp;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.charts;

/// <summary>Line chart with points.</summary>
public sealed class LineChartRenderer
{
    public byte[] RenderPng(
        string title,
        string xAxisLabel,
        string yAxisLabel,
        IReadOnlyList<string> xLabels,
        IReadOnlyList<double> yValues,
        int width = 1200,
        int height = 700)
    {
        if (xLabels.Count != yValues.Count) throw new ArgumentException("xLabels and yValues must have the same length.");
        if (xLabels.Count == 0) throw new ArgumentException("No data to render.");

        var padding = 30f;
        var titleHeight = 70f;
        var leftMargin = 110f;
        var rightMargin = 30f;

        // Keep generous bottom margin for vertical labels
        var bottomMargin = 230f;

        var topMargin = padding + titleHeight;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        var (titlePaint, labelPaint, smallPaint, axisPaint, gridPaint, valuePaint) = ChartRenderHelpers.CreateDefaultPaints();
        using var linePaint = new SKPaint { IsAntialias = true, Color = new SKColor(60, 110, 200), StrokeWidth = 3, Style = SKPaintStyle.Stroke };
        using var pointPaint = new SKPaint { IsAntialias = true, Color = new SKColor(60, 110, 200), Style = SKPaintStyle.Fill };

        ChartRenderHelpers.DrawTitle(canvas, title, width, padding, titlePaint);

        var plotLeft = leftMargin;
        var plotTop = topMargin;
        var plotRight = width - rightMargin;
        var plotBottom = height - bottomMargin;

        var plotW = plotRight - plotLeft;
        var plotH = plotBottom - plotTop;

        var maxVal = Math.Max(0, yValues.Max());
        var yMax = ChartRenderHelpers.NiceCeiling(maxVal);
        if (yMax <= 0) yMax = 1;

        ChartRenderHelpers.DrawAxesAndYGrid(canvas, plotLeft, plotTop, plotRight, plotBottom, plotH, yMax, 5, axisPaint, gridPaint, smallPaint);

        var n = xLabels.Count;
        var stepX = n == 1 ? 0 : (plotW / (n - 1));

        // Requirement: vertical labels under the axis (always)
        var rotateLabels = true;
        var labelRotation = 90f;  // +90 => goes downward from axis
        var labelYOffset = 10f;   // distance below x-axis line

        // Build path
        var path = new SKPath();
        for (int i = 0; i < n; i++)
        {
            var x = plotLeft + (i * stepX);
            var v = Math.Max(0, yValues[i]);
            var y = plotBottom - (float)(v / yMax) * plotH;

            if (i == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);

            canvas.DrawCircle(x, y, 4.5f, pointPaint);

            // X labels
            var label = ChartRenderHelpers.TrimLabel(xLabels[i] ?? string.Empty, 24);

            if (!rotateLabels)
            {
                var lw = smallPaint.MeasureText(label);
                canvas.DrawText(label, x - lw / 2f, plotBottom + 22f, smallPaint);
            }
            else
            {
                // Vertical labels BELOW the axis, centered under the point.
                var bounds = new SKRect();
                smallPaint.MeasureText(label, ref bounds);

                canvas.Save();
                canvas.Translate(x, plotBottom + labelYOffset);
                canvas.RotateDegrees(labelRotation);

                // Place the text so it starts below the axis (no overlap):
                // - X: shift by -bounds.Left to avoid clipping from left bearing
                // - Y: center by -bounds.MidY so it's centered under the tick/point
                canvas.DrawText(label, -bounds.Left, -bounds.MidY, smallPaint);

                canvas.Restore();
            }
        }

        canvas.DrawPath(path, linePaint);

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
