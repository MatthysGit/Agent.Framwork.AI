using SkiaSharp;

namespace Ai.AgentFramwork.Massar.Web.Services.Chat.charts;

/// <summary>
/// Small shared helpers for chart renderers.
/// </summary>
internal static class ChartRenderHelpers
{
    public static double NiceCeiling(double max)
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

    public static string FormatTick(double v)
    {
        if (v >= 1_000_000) return (v / 1_000_000d).ToString("0.#") + "M";
        if (v >= 1_000) return (v / 1_000d).ToString("0.#") + "K";
        return v.ToString("0");
    }

    public static string FormatValue(double v)
    {
        if (Math.Abs(v - Math.Round(v)) < 1e-9) return ((long)Math.Round(v)).ToString();
        return v.ToString("0.##");
    }

    public static string TrimLabel(string s, int maxLen)
        => string.IsNullOrEmpty(s) ? string.Empty
         : (s.Length <= maxLen ? s : s.Substring(0, Math.Max(1, maxLen - 1)) + "…");

    public static void DrawTitle(SKCanvas canvas, string? title, int width, float padding, SKPaint titlePaint)
    {
        var titleText = string.IsNullOrWhiteSpace(title) ? "Chart" : title.Trim();
        var bounds = new SKRect();
        titlePaint.MeasureText(titleText, ref bounds);
        canvas.DrawText(titleText, (width - bounds.Width) / 2f, padding + 30f, titlePaint);
    }

    public static byte[] EncodePng(SKSurface surface)
    {
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static (SKPaint title, SKPaint label, SKPaint small, SKPaint axis, SKPaint grid, SKPaint value) CreateDefaultPaints()
    {
        var title = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black,
            TextSize = 28,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        var label = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black,
            TextSize = 16,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };

        var small = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black,
            TextSize = 14,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };

        var axis = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(30, 30, 30),
            StrokeWidth = 2,
            Style = SKPaintStyle.Stroke
        };

        var grid = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(220, 220, 220),
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke
        };

        var value = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black,
            TextSize = 14,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        return (title, label, small, axis, grid, value);
    }

    public static void DrawAxesAndYGrid(
        SKCanvas canvas,
        float plotLeft, float plotTop, float plotRight, float plotBottom,
        float plotH,
        double yMax,
        int tickCount,
        SKPaint axisPaint,
        SKPaint gridPaint,
        SKPaint smallPaint)
    {
        canvas.DrawLine(plotLeft, plotTop, plotLeft, plotBottom, axisPaint);
        canvas.DrawLine(plotLeft, plotBottom, plotRight, plotBottom, axisPaint);

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
    }

    public static void ValidateMultiSeries(
        IReadOnlyList<string> xLabels,
        IReadOnlyList<string> seriesNames,
        IReadOnlyList<IReadOnlyList<double>> seriesValues)
    {
        if (xLabels.Count == 0) throw new ArgumentException("No xLabels.");
        if (seriesNames.Count == 0) throw new ArgumentException("No seriesNames.");
        if (seriesValues.Count != seriesNames.Count) throw new ArgumentException("seriesValues count must equal seriesNames count.");

        for (int s = 0; s < seriesValues.Count; s++)
        {
            if (seriesValues[s].Count != xLabels.Count)
                throw new ArgumentException($"Series '{seriesNames[s]}' values count must match xLabels count.");
        }
    }

    public static SKColor SeriesColor(int i, int seriesCount)
    {
        // Deterministic palette using HSL (no dependencies)
        var hue = (i * 360f / Math.Max(1, seriesCount)) % 360f;
        return SKColor.FromHsl(hue, 60, 50);
    }

    public static void DrawLegend(
        SKCanvas canvas,
        float x,
        float y,
        IReadOnlyList<string> seriesNames,
        Func<int, SKColor> colorForSeries,
        SKPaint textPaint)
    {
        var rowH = 22f;
        for (int i = 0; i < seriesNames.Count; i++)
        {
            using var swatch = new SKPaint { IsAntialias = true, Color = colorForSeries(i), Style = SKPaintStyle.Fill };
            canvas.DrawRect(new SKRect(x, y + i * rowH - 12, x + 14, y + i * rowH + 2), swatch);

            var name = TrimLabel(seriesNames[i] ?? string.Empty, 24);
            canvas.DrawText(name, x + 20, y + i * rowH, textPaint);
        }
    }
}