using SkiaSharp;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.charts;

/// <summary>
/// Multi-series column chart (grouped columns per category).
/// </summary>
public sealed class MultiSeriesColumnChartRenderer
{
    public byte[] RenderPng(
        string title,
        string xAxisLabel,
        string yAxisLabel,
        IReadOnlyList<string> xLabels,
        IReadOnlyList<string> seriesNames,
        IReadOnlyList<IReadOnlyList<double>> seriesValues,
        int width = 1300,
        int height = 750)
    {
        ChartRenderHelpers.ValidateMultiSeries(xLabels, seriesNames, seriesValues);

        var padding = 30f;
        var titleHeight = 70f;
        var leftMargin = 110f;
        var rightMargin = 240f;   // legend
        var bottomMargin = 230f;
        var topMargin = padding + titleHeight;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        var (titlePaint, labelPaint, smallPaint, axisPaint, gridPaint, valuePaint) = ChartRenderHelpers.CreateDefaultPaints();
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
        var seriesCount = seriesNames.Count;

        var slotW = plotW / n;
        var groupPad = MathF.Min(18f, slotW * 0.18f);
        var usable = MathF.Max(1f, slotW - groupPad);
        var barW = MathF.Min(28f, usable / Math.Max(1, seriesCount)); // keep bars readable
        var totalGroupW = barW * seriesCount;
        var groupLeftOffset = totalGroupW / 2f;

        var rotateLabels = n > 8 || slotW < 95f;
        var labelRotation = -40f;
        var labelYOffset = rotateLabels ? 45f : 22f;

        for (int i = 0; i < n; i++)
        {
            var cx = plotLeft + (i + 0.5f) * slotW;

            // draw series bars for category i
            for (int s = 0; s < seriesCount; s++)
            {
                var v = Math.Max(0, seriesValues[s][i]);
                var barH = (float)(v / yMax) * plotH;

                var x0 = cx - groupLeftOffset + (s * barW);
                var y0 = plotBottom - barH;

                using var barPaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = ChartRenderHelpers.SeriesColor(s, seriesCount),
                    Style = SKPaintStyle.Fill
                };

                canvas.DrawRect(new SKRect(x0, y0, x0 + barW - 2f, plotBottom), barPaint);

                // value labels only when not too crowded
                if (n <= 12 && seriesCount <= 3)
                {
                    var valueText = ChartRenderHelpers.FormatValue(seriesValues[s][i]);
                    var tw = valuePaint.MeasureText(valueText);
                    canvas.DrawText(valueText, x0 + (barW - 2f) / 2f - tw / 2f, y0 - 6, valuePaint);
                }
            }

            // x label
            var label = ChartRenderHelpers.TrimLabel(xLabels[i] ?? string.Empty, 24);
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

        titlePaint.Dispose(); labelPaint.Dispose(); smallPaint.Dispose(); axisPaint.Dispose(); gridPaint.Dispose(); valuePaint.Dispose();
        return ChartRenderHelpers.EncodePng(surface);
    }
}