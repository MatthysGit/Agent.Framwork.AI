using SkiaSharp;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.charts;

/// <summary>
/// Multi-series line chart.
/// </summary>
public sealed class MultiSeriesLineChartRenderer
{
    public byte[] RenderPng(
        string title,
        string xAxisLabel,
        string yAxisLabel,
        IReadOnlyList<string> xLabels,
        IReadOnlyList<string> seriesNames,
        IReadOnlyList<IReadOnlyList<double>> seriesValues,
        int width = 650,
        int height = 375)
    {
        ChartRenderHelpers.ValidateMultiSeries(xLabels, seriesNames, seriesValues);

        var padding = 30f;
        var titleHeight = 70f;
        var leftMargin = 110f;
        var rightMargin = 240f;   // room for legend
        var bottomMargin = 230f;
        var topMargin = padding + titleHeight;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        var (titlePaint, labelPaint, smallPaint, axisPaint, gridPaint, _) = ChartRenderHelpers.CreateDefaultPaints();
        ChartRenderHelpers.DrawTitle(canvas, title, width, padding, titlePaint);

        var plotLeft = leftMargin;
        var plotTop = topMargin;
        var plotRight = width - rightMargin;
        var plotBottom = height - bottomMargin;

        var plotW = plotRight - plotLeft;
        var plotH = plotBottom - plotTop;

        var maxVal = 0d;
        for (int s = 0; s < seriesValues.Count; s++)
            maxVal = Math.Max(maxVal, seriesValues[s].Max());

        var yMax = ChartRenderHelpers.NiceCeiling(Math.Max(0, maxVal));
        if (yMax <= 0) yMax = 1;

        ChartRenderHelpers.DrawAxesAndYGrid(canvas, plotLeft, plotTop, plotRight, plotBottom, plotH, yMax, 5, axisPaint, gridPaint, smallPaint);

        var n = xLabels.Count;
        var stepX = n == 1 ? 0 : (plotW / (n - 1));
        var rotateLabels = n > 10 || stepX < 90f;
        var labelRotation = -40f;
        var labelYOffset = rotateLabels ? 45f : 22f;

        // X labels (once)
        for (int i = 0; i < n; i++)
        {
            var x = plotLeft + (i * stepX);
            var label = ChartRenderHelpers.TrimLabel(xLabels[i] ?? string.Empty, 24);

            if (!rotateLabels)
            {
                var lw = smallPaint.MeasureText(label);
                canvas.DrawText(label, x - lw / 2f, plotBottom + labelYOffset, smallPaint);
            }
            else
            {
                var bounds = new SKRect();
                smallPaint.MeasureText(label, ref bounds);

                canvas.Save();
                canvas.Translate(x, plotBottom + labelYOffset);
                canvas.RotateDegrees(labelRotation);
                canvas.DrawText(label, -bounds.MidX, -bounds.Top, smallPaint);
                canvas.Restore();
            }
        }

        // Series
        for (int s = 0; s < seriesValues.Count; s++)
        {
            var color = ChartRenderHelpers.SeriesColor(s, seriesValues.Count);

            using var linePaint = new SKPaint { IsAntialias = true, Color = color, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
            using var pointPaint = new SKPaint { IsAntialias = true, Color = color, Style = SKPaintStyle.Fill };

            var path = new SKPath();

            for (int i = 0; i < n; i++)
            {
                var x = plotLeft + (i * stepX);
                var v = Math.Max(0, seriesValues[s][i]);
                var y = plotBottom - (float)(v / yMax) * plotH;

                if (i == 0) path.MoveTo(x, y);
                else path.LineTo(x, y);

                canvas.DrawCircle(x, y, 4.5f, pointPaint);
            }

            canvas.DrawPath(path, linePaint);
        }

        // Legend
        var legendX = plotRight + 30f;
        var legendY = plotTop + 20f;
        ChartRenderHelpers.DrawLegend(canvas, legendX, legendY, seriesNames, i => ChartRenderHelpers.SeriesColor(i, seriesNames.Count), smallPaint);

        // Axis labels
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

        titlePaint.Dispose(); labelPaint.Dispose(); smallPaint.Dispose(); axisPaint.Dispose(); gridPaint.Dispose();
        return ChartRenderHelpers.EncodePng(surface);
    }
}