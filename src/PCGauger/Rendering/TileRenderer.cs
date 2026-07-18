using SkiaSharp;

namespace PCGauger.Rendering;

/// <summary>
/// Draws the dashboard tiles onto a SkiaSharp surface. Each tile is a rounded
/// card with a title, a big primary value, a usage bar, and a sparkline of
/// recent history. Layout is computed from the tile rectangle so it adapts to
/// window size (manual positioning on a small display in v1).
/// </summary>
public sealed class TileRenderer
{
    private readonly Theme _theme;

    // Minimum vertical height reserved for each tile's sparkline, so the graph
    // stays legible on a small display instead of collapsing to a thin line.
    private const float MinSparklineHeight = 90;

    public TileRenderer(Theme theme) => _theme = theme;

    public void DrawCpuTile(SKCanvas canvas, SKRect rect, double usagePct, uint clockMhz, int cores, IReadOnlyList<(DateTimeOffset, double)> history)
    {
        DrawCard(canvas, rect);
        float pad = 16;
        float x = rect.Left + pad;
        float y = rect.Top + pad;
        float w = rect.Width - pad * 2;

        DrawTitle(canvas, x, y, "CPU");
        y += 30;

        // Big usage percentage.
        DrawBigValue(canvas, x, y, $"{usagePct:0.#}", "%");
        y += 56;

        // Usage bar.
        DrawBar(canvas, x, y, w, usagePct / 100.0);
        y += 22;

        // Sparkline anchored from the bottom with a guaranteed minimum height,
        // so it stays readable on a small display even when the tile is short.
        float sparkBottom = rect.Bottom - pad;
        float sparkTop = sparkBottom - MinSparklineHeight;
        if (sparkTop < y + 4) sparkTop = y + 4;
        DrawSparkline(canvas, new SKRect(x, sparkTop, rect.Right - pad, sparkBottom), history, 0, 100);

        // Secondary line sits just above the sparkline.
        DrawSecondary(canvas, x, sparkTop - 18, $"{Format.Mhz(clockMhz)}   •   {cores} cores / {cores * 2} threads");
    }

    public void DrawRamTile(SKCanvas canvas, SKRect rect, double usagePct, ulong used, ulong total)
    {
        DrawCard(canvas, rect);
        float pad = 16;
        float x = rect.Left + pad;
        float y = rect.Top + pad;
        float w = rect.Width - pad * 2;

        DrawTitle(canvas, x, y, "RAM");
        y += 30;

        DrawBigValue(canvas, x, y, $"{usagePct:0.#}", "%");
        y += 56;

        DrawBar(canvas, x, y, w, usagePct / 100.0);
        y += 22;

        float sparkBottom = rect.Bottom - pad;
        float sparkTop = sparkBottom - MinSparklineHeight;
        if (sparkTop < y + 4) sparkTop = y + 4;
        DrawSparkline(canvas, new SKRect(x, sparkTop, rect.Right - pad, sparkBottom), _ramHistory, 0, 100);

        DrawSecondary(canvas, x, sparkTop - 18, $"{Format.Bytes(used)} / {Format.Bytes(total)}");
    }

    // RAM history is supplied externally because the poller owns the buffer.
    private IReadOnlyList<(DateTimeOffset, double)> _ramHistory = Array.Empty<(DateTimeOffset, double)>();
    public void SetRamHistory(IReadOnlyList<(DateTimeOffset, double)> history) => _ramHistory = history;

    // ---- primitives ----

    private void DrawCard(SKCanvas canvas, SKRect rect)
    {
        using var round = new SKRoundRect(rect, 14);
        using var paint = _theme.TilePaint();
        canvas.DrawRoundRect(round, paint);
        using var border = _theme.TileBorderPaint();
        canvas.DrawRoundRect(round, border);
    }

    private void DrawTitle(SKCanvas canvas, float x, float y, string text)
    {
        using var paint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semibold"), 15);
        canvas.DrawText(text, x, y + 14, font, paint);
    }

    private void DrawBigValue(SKCanvas canvas, float x, float y, string value, string suffix)
    {
        using var paint = new SKPaint { Color = _theme.TextPrimary, IsAntialias = true };
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI Light"), 46);
        canvas.DrawText(value, x, y + 40, font, paint);

        using var suffixPaint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        using var suffixFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semilight"), 20);
        float valueWidth = font.MeasureText(value);
        canvas.DrawText(suffix, x + valueWidth + 6, y + 40, suffixFont, suffixPaint);
    }

    private void DrawSecondary(SKCanvas canvas, float x, float y, string text)
    {
        using var paint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 14);
        canvas.DrawText(text, x, y + 14, font, paint);
    }

    private void DrawBar(SKCanvas canvas, float x, float y, float w, double fraction)
    {
        float h = 10;
        fraction = Math.Max(0, Math.Min(1, fraction));
        using var track = new SKRoundRect(new SKRect(x, y, x + w, y + h), 5);
        using var trackPaint = new SKPaint { Color = _theme.AccentSoft, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(track, trackPaint);

        float fillW = Math.Max(0, w * (float)fraction);
        if (fillW > 1)
        {
            using var fill = new SKRoundRect(new SKRect(x, y, x + fillW, y + h), 5);
            using var fillPaint = new SKPaint { Color = _theme.Accent, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(fill, fillPaint);
        }
    }

    private void DrawSparkline(SKCanvas canvas, SKRect rect, IReadOnlyList<(DateTimeOffset, double)> history, double min, double max)
    {
        if (history.Count < 2) return;
        float pad = 4;
        float x0 = rect.Left + pad;
        float y0 = rect.Top + pad;
        float w = rect.Width - pad * 2;
        float h = rect.Height - pad * 2;
        if (w <= 0 || h <= 0) return;

        double range = max - min;
        if (range <= 0) range = 1;

        using var path = new SKPath();
        for (int i = 0; i < history.Count; i++)
        {
            double norm = (history[i].Item2 - min) / range;
            norm = Math.Max(0, Math.Min(1, norm));
            float px = x0 + (float)i / (history.Count - 1) * w;
            float py = y0 + h - (float)norm * h;
            if (i == 0) path.MoveTo(px, py);
            else path.LineTo(px, py);
        }

        using var fillPath = new SKPath(path);
        fillPath.LineTo(x0 + w, y0 + h);
        fillPath.LineTo(x0, y0 + h);
        fillPath.Close();

        using var fillPaint = new SKPaint
        {
            Color = new SKColor(_theme.Sparkline.Red, _theme.Sparkline.Green, _theme.Sparkline.Blue, 40),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        canvas.DrawPath(fillPath, fillPaint);

        using var linePaint = new SKPaint
        {
            Color = _theme.Sparkline,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true,
        };
        canvas.DrawPath(path, linePaint);
    }
}
