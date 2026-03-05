using SkiaSharp;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.charts;

public sealed class BarChartRenderer
{
    public byte[] RenderPng(
        string title,
        string xAxisLabel,
        string yAxisLabel,
        IReadOnlyList<string> labels,
        IReadOnlyList<double> values,
        int width = 1300,
        int height = 700)
    {
        if (labels.Count != values.Count)
            throw new ArgumentException("labels and values must have the same length.");

        if (labels.Count == 0)
            throw new ArgumentException("No data to render.");

        // ----------------------------
        // Layout
        // ----------------------------
        var padding = 30f;
        var titleHeight = 70f;
        var leftMargin = 110f;     // room for Y ticks + y-axis label
        var rightMargin = 30f;
        var bottomMargin = 230f;   // increased to prevent rotated labels overlapping bars
        var topMargin = padding + titleHeight;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        // ----------------------------
        // Paints
        // ----------------------------
        using var titlePaint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black,
            TextSize = 28,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        using var labelPaint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black,
            TextSize = 16,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };

        using var smallPaint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black,
            TextSize = 14,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };

        using var axisPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(30, 30, 30),
            StrokeWidth = 2,
            Style = SKPaintStyle.Stroke
        };

        using var gridPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(220, 220, 220),
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke
        };

        using var barPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(60, 110, 200),
            Style = SKPaintStyle.Fill
        };

        using var valuePaint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black,
            TextSize = 14,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        // ----------------------------
        // Title
        // ----------------------------
        var titleText = string.IsNullOrWhiteSpace(title) ? "Bar Chart" : title.Trim();
        var titleBounds = new SKRect();
        titlePaint.MeasureText(titleText, ref titleBounds);
        canvas.DrawText(titleText, (width - titleBounds.Width) / 2f, padding + 30f, titlePaint);

        // ----------------------------
        // Plot area
        // ----------------------------
        var plotLeft = leftMargin;
        var plotTop = topMargin;
        var plotRight = width - rightMargin;
        var plotBottom = height - bottomMargin;

        var plotW = plotRight - plotLeft;
        var plotH = plotBottom - plotTop;

        // Axes
        canvas.DrawLine(plotLeft, plotTop, plotLeft, plotBottom, axisPaint);
        canvas.DrawLine(plotLeft, plotBottom, plotRight, plotBottom, axisPaint);

        // ----------------------------
        // Y scale
        // ----------------------------
        var maxVal = values.Max();
        if (maxVal < 0) maxVal = 0;

        var yMax = NiceCeiling(maxVal);
        if (yMax <= 0) yMax = 1;

        // Y ticks / grid
        const int tickCount = 5;
        for (int i = 0; i <= tickCount; i++)
        {
            var t = i / (float)tickCount;
            var y = plotBottom - (t * plotH);

            canvas.DrawLine(plotLeft, y, plotRight, y, gridPaint);

            var val = yMax * t;
            var text = FormatTick(val);
            var tw = smallPaint.MeasureText(text);
            canvas.DrawText(text, plotLeft - 10 - tw, y + 5, smallPaint);
        }

        // ----------------------------
        // Bars + X labels
        // ----------------------------
        var n = labels.Count;
        var slotW = plotW / n;
        var barW = MathF.Min(60f, slotW * 0.65f);

        // Requirement: X-axis category labels must be vertical under the bars.
        // Keep this enabled always for consistent output.
        var rotateLabels = true;
        // Rotate +90 so the text "flows" downward (away from the plot area), keeping labels below the bars.
        var labelRotation = 90f;
        var labelYOffset = 18f; // distance below the x-axis line

        for (int i = 0; i < n; i++)
        {
            var v = values[i];
            if (v < 0) v = 0;

            var barH = (float)(v / yMax) * plotH;
            var cx = plotLeft + (i + 0.5f) * slotW;
            var x0 = cx - (barW / 2f);
            var y0 = plotBottom - barH;

            // bar rect
            var rect = new SKRect(x0, y0, x0 + barW, plotBottom);
            canvas.DrawRect(rect, barPaint);

            // value label above bar
            var valueText = FormatValue(values[i]);
            var vtW = valuePaint.MeasureText(valueText);
            canvas.DrawText(valueText, cx - vtW / 2f, y0 - 6, valuePaint);

            // x label
            var label = labels[i] ?? string.Empty;
            label = TrimLabel(label, 24);

            if (!rotateLabels)
            {
                var lw = smallPaint.MeasureText(label);
                canvas.DrawText(label, cx - lw / 2f, plotBottom + 22f, smallPaint);
            }
            else
            {
                // Vertical labels under each bar.
                // Rotate +90 so the text extends DOWN from the axis (doesn't overlap the plot/bars).
                var bounds = new SKRect();
                smallPaint.MeasureText(label, ref bounds);

                canvas.Save();
                canvas.Translate(cx, plotBottom + labelYOffset);
                canvas.RotateDegrees(labelRotation);

                // After rotation:
                // - Local +X becomes "down" in screen space.
                // - Local +Y becomes "left" in screen space.
                // To keep the label centered under the bar, center the glyphs in local Y.
                // To ensure the label starts below the axis, shift in local X by -bounds.Left.
                var x = -bounds.Left; // start right at the axis and flow downward
                var y = -bounds.MidY; // center under the bar

                canvas.DrawText(label, x, y, smallPaint);

                canvas.Restore();
            }
        }

        // ----------------------------
        // Axis labels
        // ----------------------------
        if (!string.IsNullOrWhiteSpace(xAxisLabel))
        {
            var xText = xAxisLabel.Trim();
            var xw = labelPaint.MeasureText(xText);
            canvas.DrawText(xText, plotLeft + (plotW - xw) / 2f, height - 40, labelPaint);
        }

        if (!string.IsNullOrWhiteSpace(yAxisLabel))
        {
            var yText = yAxisLabel.Trim();
            canvas.Save();
            canvas.Translate(35, plotTop + plotH / 2f);
            canvas.RotateDegrees(-90);
            var yw = labelPaint.MeasureText(yText);
            canvas.DrawText(yText, -yw / 2f, 0, labelPaint);
            canvas.Restore();
        }

        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);

        // Encode() can (rarely) return null/empty if Skia initialization fails or the surface is invalid.
        // Failing fast here prevents returning a "blank" or 0-byte image, and lets the orchestrator retry.
        if (data is null || data.Size == 0)
            throw new InvalidOperationException("Failed to encode chart image (PNG encoder returned empty data).");

        return data.ToArray();
    }

    private static double NiceCeiling(double max)
    {
        if (max <= 0) return 1;

        var exp = Math.Floor(Math.Log10(max));
        var pow = Math.Pow(10, exp);
        var frac = max / pow;

        var niceFrac =
            frac <= 1 ? 1 :
            frac <= 2 ? 2 :
            frac <= 5 ? 5 : 10;

        return niceFrac * pow;
    }

    private static string FormatTick(double v)
    {
        if (v >= 1_000_000) return (v / 1_000_000d).ToString("0.#") + "M";
        if (v >= 1_000) return (v / 1_000d).ToString("0.#") + "K";
        return v.ToString("0");
    }

    private static string FormatValue(double v)
    {
        if (Math.Abs(v - Math.Round(v)) < 1e-9) return ((long)Math.Round(v)).ToString();
        return v.ToString("0.##");
    }

    private static string TrimLabel(string s, int maxLen)
        => s.Length <= maxLen ? s : s.Substring(0, maxLen - 1) + "…";
}
