using PCGauger.Infrastructure;
using SkiaSharp;

namespace PCGauger.Rendering;

/// <summary>
/// Draws the dashboard tiles onto a SkiaSharp surface. Each tile is a rounded
/// card with a title, a big primary value, a usage bar, and a sparkline of
/// recent history. Layout is computed from the tile rectangle so it adapts to
/// window size (manual positioning on a small display in v1).
///
/// Rendering is driven by each tile's <see cref="TileSettings"/> and a resolved
/// per-kind accent (<see cref="TilePalette"/>), not by theme globals. The active
/// <see cref="Theme"/> is read live (Theme property) so a theme swap applies
/// instantly across all tiles, footer, and panes.
/// </summary>
public sealed partial class TileRenderer
{
    private Theme _theme;

    /// <summary>The active theme. Reassignable so a theme swap applies instantly.</summary>
    public Theme Theme { get => _theme; set => _theme = value; }

    // Minimum vertical height reserved for each tile's sparkline, so the graph
    // stays legible on a small display instead of collapsing to a thin line.
    public const float MinSparklineHeight = 90;

    // Corner affordance sizing. The gear sits to the LEFT of the grab handle;
    // both live in the top-right corner and keep hit rects >= 24px for touch.
    private const float AffordanceSize = 24;
    private const float AffordanceInset = 14;

    // Inner content inset for a tile body (left/right/top/bottom). Smaller =
    // content sits closer to the card edges with less wasted space.
    public const float TilePad = 10;

    // Settings pane geometry (compact popover; fits 5 toggles + 12 swatches +
    // Custom/Reset on one row). Clamped to the host window, not the tile.
    private const float PanePad = 12;
    private const float PaneWidth = 200;
    // Tall enough for the full non-device content: header gap + 5 toggles +
    // divider + "Accent" label + 13 swatches (3 rows of 6/6/1) + Custom/Reset
    // buttons, all inside the card. Bumped from 290 -> 316 so the swatch rows
    // (which grew to 13) no longer overflow the card's bottom edge.
    private const float PaneHeight = 316;
    private const float PaneHeaderH = 20;
    // Vertical gap reserved after the "Customize" header before the first row,
    // so the header text never collides with the first toggle. Shared by both
    // the layout math and the draw routine to keep them in sync.
    private const float PaneHeaderGap = 24;
    private const float PaneToggleH = 22;
    private const float PaneAccentLabelH = 16;
    private const float SwatchW = 24;
    private const float SwatchH = 18;
    private const float SwatchGap = 5;
    private const int SwatchPerRow = 6;
    private const float PaneButtonH = 22;

    // Global threshold-alert coloring (ITEM 6). Per-frame evaluation.
    public static bool ThresholdEnabled { get; set; }
    public static double ThresholdPercent { get; set; } = 90;
    public static SKColor AlertColor { get; } = new SKColor(0xFF, 0x52, 0x52);

    public TileRenderer(Theme theme) => _theme = theme;

    // ---- tile entry points (settings + accent aware) ----

    public void DrawCpuTile(SKCanvas canvas, SKRect rect, TileSettings s, SKColor accent, double usagePct, uint clockMhz, int physicalCores, int logicalProcessors, IReadOnlyList<(DateTimeOffset, double)> history, TimeSpan historyWindow, string? deviceSubtitle = null)
    {
        bool alert = ThresholdEnabled && usagePct >= ThresholdPercent;
        var v = new TileVisual(TileKind.Cpu, s, accent, _theme, alert) { Rect = rect, Y = rect.Top + TilePad, SparkWindowSeconds = historyWindow.TotalSeconds };
        DrawCard(canvas, rect);
        DrawTitle(canvas, v, "CPU", deviceSubtitle);
        DrawBigValue(canvas, v, usagePct, "%");
        DrawBar(canvas, v, usagePct / 100.0);
        DrawSparkline(canvas, v, history, false);
        DrawSecondary(canvas, v, $"{Format.Clock(clockMhz)}   •   {physicalCores} cores / {logicalProcessors} threads");
        v.Finish(canvas, this);
    }

    public void DrawRamTile(SKCanvas canvas, SKRect rect, TileSettings s, SKColor accent, double usagePct, ulong used, ulong total, string? deviceSubtitle = null)
    {
        bool alert = ThresholdEnabled && usagePct >= ThresholdPercent;
        var v = new TileVisual(TileKind.Ram, s, accent, _theme, alert) { Rect = rect, Y = rect.Top + TilePad, SparkWindowSeconds = _ramWindowSeconds };
        DrawCard(canvas, rect);
        DrawTitle(canvas, v, "RAM", deviceSubtitle);
        DrawBigValue(canvas, v, usagePct, "%");
        DrawBar(canvas, v, usagePct / 100.0);
        DrawSparkline(canvas, v, _ramHistory, false);
        DrawSecondary(canvas, v, $"{Format.Size(used, s.UnitMode, TileKind.Ram)} / {Format.Size(total, s.UnitMode, TileKind.Ram)}");
        v.Finish(canvas, this);
    }

    // RAM history is supplied externally because the poller owns the buffer.
    private IReadOnlyList<(DateTimeOffset, double)> _ramHistory = Array.Empty<(DateTimeOffset, double)>();
    private double _ramWindowSeconds = 60;
    public void SetRamHistory(IReadOnlyList<(DateTimeOffset, double)> history, TimeSpan window)
    {
        _ramHistory = history;
        _ramWindowSeconds = window.TotalSeconds;
    }

    private IReadOnlyList<(DateTimeOffset, double)> _gpuHistory = Array.Empty<(DateTimeOffset, double)>();
    private double _gpuWindowSeconds = 60;
    public void SetGpuHistory(IReadOnlyList<(DateTimeOffset, double)> history, TimeSpan window)
    {
        _gpuHistory = history;
        _gpuWindowSeconds = window.TotalSeconds;
    }

    public void DrawGpuTile(SKCanvas canvas, SKRect rect, TileSettings s, SKColor accent, double utilPct, ulong vramUsed, ulong vramBudget, string? deviceSubtitle = null)
    {
        bool alert = ThresholdEnabled && utilPct >= ThresholdPercent;
        var v = new TileVisual(TileKind.Gpu, s, accent, _theme, alert) { Rect = rect, Y = rect.Top + TilePad, SparkWindowSeconds = _gpuWindowSeconds };
        DrawCard(canvas, rect);
        DrawTitle(canvas, v, "GPU", deviceSubtitle);
        DrawBigValue(canvas, v, utilPct, "%");
        DrawBar(canvas, v, utilPct / 100.0);
        DrawSparkline(canvas, v, _gpuHistory, false);
        string vram = vramBudget > 0
            ? $"{Format.Size(vramUsed, s.UnitMode, TileKind.Gpu)} / {Format.Size(vramBudget, s.UnitMode, TileKind.Gpu)}"
            : Format.Size(vramUsed, s.UnitMode, TileKind.Gpu);
        DrawSecondary(canvas, v, vram);
        v.Finish(canvas, this);
    }

    private IReadOnlyList<(DateTimeOffset, double)> _diskReadHistory = Array.Empty<(DateTimeOffset, double)>();
    private IReadOnlyList<(DateTimeOffset, double)> _diskWriteHistory = Array.Empty<(DateTimeOffset, double)>();
    private double _diskWindowSeconds = 60;
    public void SetDiskHistory(IReadOnlyList<(DateTimeOffset, double)> read, IReadOnlyList<(DateTimeOffset, double)> write, TimeSpan window)
    {
        _diskReadHistory = read;
        _diskWriteHistory = write;
        _diskWindowSeconds = window.TotalSeconds;
    }

    private IReadOnlyList<(DateTimeOffset, double)> _netHistory = Array.Empty<(DateTimeOffset, double)>();
    private IReadOnlyList<(DateTimeOffset, double)> _netUpHistory = Array.Empty<(DateTimeOffset, double)>();
    private double _netWindowSeconds = 60;
    public void SetNetHistory(IReadOnlyList<(DateTimeOffset, double)> down, IReadOnlyList<(DateTimeOffset, double)> up, TimeSpan window)
    {
        _netHistory = down;
        _netUpHistory = up;
        _netWindowSeconds = window.TotalSeconds;
    }

    public void DrawDiskTile(SKCanvas canvas, SKRect rect, TileSettings s, SKColor accent, double usagePct, ulong used, ulong total, double bytesPerSec, string? deviceSubtitle = null)
    {
        bool alert = ThresholdEnabled && usagePct >= ThresholdPercent;
        var v = new TileVisual(TileKind.Disk, s, accent, _theme, alert) { Rect = rect, Y = rect.Top + TilePad, SparkWindowSeconds = _diskWindowSeconds };
        DrawCard(canvas, rect);
        DrawTitle(canvas, v, "DISK", deviceSubtitle);
        DrawBigValue(canvas, v, usagePct, "%");
        DrawBar(canvas, v, usagePct / 100.0);
        // Dual graph: write (accent) + read (blue) throughput in one sparkline.
        DrawSparkline(canvas, v, _diskWriteHistory, true, _diskReadHistory, TilePalette.DiskRead, "W", "R");
        DrawSecondary(canvas, v, $"{Format.Size(used, s.UnitMode, TileKind.Disk)} / {Format.Size(total, s.UnitMode, TileKind.Disk)}   •   {Format.Rate((ulong)bytesPerSec, s.UnitMode, TileKind.Disk)}");
        v.Finish(canvas, this);
    }

    /// <summary>
    /// Network throughput tile. Big value = current download rate (matches the
    /// disk tile's "/s" throughput style via Format.Bytes). There is no natural
    /// percentage, so the usage bar is intentionally omitted — the sparkline
    /// grows to fill the freed space. NET is exempt from the threshold-alert
    /// path (no usagePct).
    /// </summary>
    public void DrawNetworkTile(SKCanvas canvas, SKRect rect, TileSettings s, SKColor accent, double downBps, double upBps, string interfaceName, string? deviceSubtitle = null)
    {
        // No alert path: NET has no usage percentage.
        var v = new TileVisual(TileKind.Network, s, accent, _theme, false) { Rect = rect, Y = rect.Top + TilePad, SparkWindowSeconds = _netWindowSeconds };
        DrawCard(canvas, rect);
        DrawTitle(canvas, v, "NET", deviceSubtitle);
        // Big value = download rate, formatted like the disk tile's throughput.
        DrawBigValueLiteral(canvas, v, Format.Rate((ulong)downBps, s.UnitMode, TileKind.Network));
        // ShowUsageBar flag is consumed gracefully: nothing is drawn for the bar
        // row, and the sparkline below simply fills the remaining space.
        // Dual graph: down (accent) + up (violet) throughput in one sparkline.
        DrawSparkline(canvas, v, _netHistory, true, _netUpHistory, TilePalette.NetUp, "↓", "↑");
        string iface = Truncate(canvas, interfaceName, rect.Width - 2 * TilePad, "Segoe UI", 14);
        DrawSecondary(canvas, v, $"↑ {Format.Rate((ulong)upBps, s.UnitMode, TileKind.Network)}   •   {iface}");
        v.Finish(canvas, this);
    }

    // ---- big-value typography (responsive) ----
    // The headline number is the tile's visual anchor, but a fixed 46px overflows
    // narrow tiles (long NET rate strings) and overwhelms short ones. Scale it
    // down when it would overflow the content width or dominate a short tile,
    // with a hard floor so it always stays prominent.
    private const float BigValueMaxFont = 46f;
    private const float BigValueMinFont = 26f;
    // Baseline offset and vertical advance, as fractions of the font size (kept
    // proportional to the original 46px / +40 baseline / +56 advance).
    private const float BigValueBaselineRatio = 40f / 46f;
    private const float BigValueAdvanceRatio = 56f / 46f;

    /// <summary>Computes the big-value font size for a tile, scaled down from
    /// <see cref="BigValueMaxFont"/> to fit the content width (value + suffix) and
    /// the tile height, clamped to <see cref="BigValueMinFont"/>.</summary>
    private float BigValueFontSize(SKCanvas canvas, TileVisual v, string text, string? suffix)
    {
        float availW = v.Rect.Width - 2 * TilePad;
        float size = BigValueMaxFont;

        // Width constraint: value + suffix (+gap) must fit the content width.
        using (var probe = new SKFont(SKTypeface.FromFamilyName("Segoe UI Light"), size))
        {
            float textW = probe.MeasureText(text);
            float suffixW = 0;
            if (!string.IsNullOrEmpty(suffix))
            {
                using var sfx = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semilight"), size * (20f / BigValueMaxFont));
                suffixW = sfx.MeasureText(suffix) + 6;
            }
            float totalW = textW + suffixW;
            if (totalW > availW && totalW > 0)
                size = Math.Min(size, size * (availW / totalW));
        }

        // Height constraint: keep the headline from overwhelming a short tile.
        size = Math.Min(size, v.Rect.Height * 0.30f);

        return Math.Clamp(size, BigValueMinFont, BigValueMaxFont);
    }

    /// <summary>
    /// Draws a pre-formatted big value (used by NET, whose rate is a byte string
    /// rather than a bare percentage). Mirrors <see cref="DrawBigValue"/>'s
    /// layout and vertical advance.
    /// </summary>
    private void DrawBigValueLiteral(SKCanvas canvas, TileVisual v, string literal)
    {
        if (!v.Settings.ShowBigValue) return;
        float x = v.Rect.Left + TilePad;
        float size = BigValueFontSize(canvas, v, literal, null);
        float baseline = v.Y + size * BigValueBaselineRatio;
        using var paint = new SKPaint { Color = v.ValueColor, IsAntialias = true };
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI Light"), size);
        canvas.DrawText(literal, x, baseline, font, paint);
        v.Y += size * BigValueAdvanceRatio;
    }

    /// <summary>Truncates text with an ellipsis so it never overflows maxWidth.</summary>
    private static string Truncate(SKCanvas canvas, string text, float maxWidth, string family, int size)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        using var f = new SKFont(SKTypeface.FromFamilyName(family), size);
        if (f.MeasureText(text) <= maxWidth) return text;
        string ell = "…";
        var sb = new System.Text.StringBuilder(text);
        while (sb.Length > 1 && f.MeasureText(sb.ToString() + ell) > maxWidth)
            sb.Remove(sb.Length - 1, 1);
        return sb.ToString() + ell;
    }

    /// <summary>
    /// Draws text clipped to <paramref name="maxX"/>; when it's too wide it
    /// dissolves into <paramref name="fadeInto"/> over the last few px (matching
    /// the footer status-bar fade) instead of hard-clipping or overflowing the
    /// card. Used for tile text that must never spill past the card's right edge.
    /// </summary>
    internal void DrawTextFaded(SKCanvas canvas, string text, float x, float baseline, float maxX, SKFont font, SKPaint paint, SKColor fadeInto)
    {
        const float FadeWidth = 28f;
        if (string.IsNullOrEmpty(text)) return;
        float avail = maxX - x;
        if (avail <= 0) return;
        if (font.MeasureText(text) <= avail)
        {
            canvas.DrawText(text, x, baseline, font, paint);
            return;
        }

        // Clip so glyphs can't spill past the boundary, draw them, then dissolve
        // the tail into the tile background.
        var clip = new SKRect(x, baseline - font.Size * 1.4f, maxX, baseline + font.Size * 0.45f);
        canvas.Save();
        canvas.ClipRect(clip);
        canvas.DrawText(text, x, baseline, font, paint);
        canvas.Restore();

        float fadeStart = maxX - FadeWidth;
        using var fade = new SKPaint();
        fade.Shader = SKShader.CreateLinearGradient(
            new SKPoint(fadeStart, 0),
            new SKPoint(maxX, 0),
            new[] { fadeInto.WithAlpha(0), fadeInto.WithAlpha(255) },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        canvas.DrawRect(fadeStart, clip.Top, FadeWidth, clip.Height, fade);
    }

    // ---- card + primitives ----

    private void DrawCard(SKCanvas canvas, SKRect rect)
    {
        using var round = new SKRoundRect(rect, 14);
        using var paint = _theme.TilePaint();
        canvas.DrawRoundRect(round, paint);
        using var border = _theme.TileBorderPaint();
        canvas.DrawRoundRect(round, border);
    }

    /// <summary>
    /// The rectangle (tile-local client space) occupied by a tile's grab handle.
    /// Shared by rendering and hit-testing so they never drift apart. The handle
    /// lives in the top-right corner, clear of the title.
    /// </summary>
    public static SKRect GrabHandleRect(SKRect tile)
    {
        return new SKRect(
            tile.Right - AffordanceInset - AffordanceSize,
            tile.Top + AffordanceInset,
            tile.Right - AffordanceInset,
            tile.Top + AffordanceInset + AffordanceSize);
    }

    /// <summary>
    /// The rectangle (tile-local client space) occupied by a tile's settings
    /// gear. Sits immediately to the LEFT of the grab handle so the two never
    /// collide; each keeps a >= 24px hit rect for touch.
    /// </summary>
    public static SKRect GearRect(SKRect tile)
    {
        var handle = GrabHandleRect(tile);
        float gap = 6;
        return new SKRect(
            handle.Left - gap - AffordanceSize,
            handle.Top,
            handle.Left - gap,
            handle.Bottom);
    }

    /// <summary>
    /// Draws the per-tile grab handle: a 2x3 dot grip. On hover it gets a
    /// rounded highlight and accent-colored dots so the detach affordance is
    /// obvious. This is the only control for detaching a tile.
    /// </summary>
    public void DrawGrabHandle(SKCanvas canvas, SKRect tile, bool hover, SKColor accent)
    {
        var r = GrabHandleRect(tile);
        if (hover)
        {
            using var bg = new SKRoundRect(r, 6);
            using var p = new SKPaint { Color = TilePalette.Soft(accent), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(bg, p);
        }

        float cx0 = r.MidX - 4;
        float cx1 = r.MidX + 4;
        float cy0 = r.MidY - 6;
        float cy1 = r.MidY;
        float cy2 = r.MidY + 6;
        using var dot = new SKPaint
        {
            Color = hover ? accent : _theme.TextSecondary,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        float rad = 1.6f;
        foreach (var (cx, cy) in new[] { (cx0, cy0), (cx1, cy0), (cx0, cy1), (cx1, cy1), (cx0, cy2), (cx1, cy2) })
            canvas.DrawCircle(cx, cy, rad, dot);
    }

    /// <summary>
    /// Draws the settings gear in the tile's top-right corner (left of the grab
    /// handle). On hover it gets a soft accent highlight; the gear strokes use
    /// the accent when hovered, otherwise muted secondary text color.
    /// </summary>
    public void DrawGear(SKCanvas canvas, SKRect tile, bool hover, SKColor accent)
        => DrawGearAt(canvas, GearRect(tile), hover, accent);

    /// <summary>
    /// Draws a close "×" at an arbitrary rectangle (footer status bar). Non-hover:
    /// muted secondary ×. Hover: red rounded background with a white × — the
    /// universal close affordance, legible on both dark and light themes.
    /// </summary>
    public void DrawCloseAt(SKCanvas canvas, SKRect r, bool hover)
    {
        if (hover)
        {
            using var bg = new SKRoundRect(r, 6);
            using var p = new SKPaint { Color = new SKColor(0xC4, 0x2B, 0x1C), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(bg, p);
        }

        float arm = r.Width * 0.24f; // half-length of each × arm
        float cx = r.MidX, cy = r.MidY;
        using var paint = new SKPaint
        {
            Color = hover ? SKColors.White : _theme.TextSecondary,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.8f,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true,
        };
        canvas.DrawLine(cx - arm, cy - arm, cx + arm, cy + arm, paint);
        canvas.DrawLine(cx - arm, cy + arm, cx + arm, cy - arm, paint);
    }

    /// <summary>
    /// Draws a settings COG at an arbitrary rectangle (used for both tile
    /// corners and the footer status-bar gear): 8 chunky trapezoid teeth around
    /// a solid ring with a visible center hole (donut). Hover highlight + accent
    /// on hover, muted secondary text otherwise. Hit rects are unchanged.
    /// </summary>
    public void DrawGearAt(SKCanvas canvas, SKRect r, bool hover, SKColor accent)
    {
        if (hover)
        {
            using var bg = new SKRoundRect(r, 6);
            using var p = new SKPaint { Color = TilePalette.Soft(accent), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(bg, p);
        }

        float cx = r.MidX;
        float cy = r.MidY;
        SKColor col = hover ? accent : _theme.TextSecondary;

        const int teeth = 8;
        float ringOuter = 6.6f;   // outer edge of the solid ring body
        float holeR = 2.6f;       // radius of the center hole (donut)
        float toothOuter = 9.2f;  // tip of the teeth
        // Angular half-widths (radians) at the ring and at the tooth tip.
        float baseHalf = 0.20f;
        float tipHalf = 0.13f;

        using var paint = new SKPaint { Color = col, Style = SKPaintStyle.Fill, IsAntialias = true };

        // Solid ring body as an annulus (outer circle minus center hole).
        using var body = new SKPath();
        body.AddCircle(cx, cy, ringOuter);
        body.AddCircle(cx, cy, holeR);
        body.FillType = SKPathFillType.EvenOdd;
        canvas.DrawPath(body, paint);

        // Chunky trapezoid teeth around the ring.
        for (int i = 0; i < teeth; i++)
        {
            double a = Math.PI * 2 * i / teeth;
            double aL = a - baseHalf, aR = a + baseHalf;
            double tL = a - tipHalf, tR = a + tipHalf;
            float bx0 = cx + (float)Math.Cos(aL) * ringOuter;
            float by0 = cy + (float)Math.Sin(aL) * ringOuter;
            float bx1 = cx + (float)Math.Cos(aR) * ringOuter;
            float by1 = cy + (float)Math.Sin(aR) * ringOuter;
            float tx0 = cx + (float)Math.Cos(tL) * toothOuter;
            float ty0 = cy + (float)Math.Sin(tL) * toothOuter;
            float tx1 = cx + (float)Math.Cos(tR) * toothOuter;
            float ty1 = cy + (float)Math.Sin(tR) * toothOuter;

            using var tooth = new SKPath();
            tooth.MoveTo(bx0, by0);
            tooth.LineTo(tx0, ty0);
            tooth.LineTo(tx1, ty1);
            tooth.LineTo(bx1, by1);
            tooth.Close();
            canvas.DrawPath(tooth, paint);
        }
    }

    /// <summary>
    /// Draws a semi-transparent "ghost" of the dragged tile that follows the
    /// cursor during a reorder. Reuses the real card styling (rounded bg, accent
    /// border, kind title) at reduced opacity so the tile reads as lifted.
    /// </summary>
    public void DrawDragGhost(SKCanvas canvas, SKRect rect, Tile tile, Theme theme)
    {
        var accent = TilePalette.DefaultFor(tile.Kind);
        canvas.SaveLayer(new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0x8C) });
        try
        {
            DrawCard(canvas, rect);
            using var border = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            using var rr = new SKRoundRect(rect, 14);
            canvas.DrawRoundRect(rr, border);
            using var tPaint = new SKPaint { Color = accent, IsAntialias = true };
            using var tFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semibold"), 15);
            canvas.DrawText(tile.Title, rect.Left + TilePad, rect.Top + 30, tFont, tPaint);
        }
        finally
        {
            canvas.Restore();
        }
    }

    /// <summary>
    /// Draws the insertion indicator for a reorder: a chunky accent bar at the
    /// leading edge of the target slot, plus a faint accent outline of that cell.
    /// <paramref name="insertAt"/> is in [0, rects.Count]; == Count means append
    /// at the end (right edge of the last cell).
    /// </summary>
    public void DrawInsertionIndicator(SKCanvas canvas, IReadOnlyList<SKRect> rects, int insertAt, SKColor accent)
    {
        if (rects.Count == 0 || insertAt < 0 || insertAt > rects.Count) return;

        SKRect cell;
        float barX;
        if (insertAt >= rects.Count)
        {
            cell = rects[^1];
            barX = cell.Right;
        }
        else
        {
            cell = rects[insertAt];
            barX = cell.Left;
        }

        // Faint accent outline marking the destination cell.
        using var cellR = new SKRoundRect(cell, 14);
        using var cellP = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawRoundRect(cellR, cellP);

        // Chunky accent bar at the leading edge.
        float bw = 4;
        float bx = barX - bw / 2;
        bx = Math.Max(2, Math.Min(bx, canvas.DeviceClipBounds.Right - 2 - bw));
        var bar = new SKRect(bx, cell.Top + 6, bx + bw, cell.Bottom - 6);
        using var barR = new SKRoundRect(bar, bw / 2);
        using var barP = new SKPaint { Color = accent, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(barR, barP);
    }

    // ---- reflow-aware content primitives ----
    // Each primitive advances v.Y by its own height (or 0 if hidden); the
    // sparkline later fills whatever vertical space remains, so toggling any
    // element off simply collapses its gap and lets the graph grow.

    private void DrawTitle(SKCanvas canvas, TileVisual v, string text, string? subtitle = null)
    {
        if (!v.Settings.ShowTitle) return;
        float x = v.Rect.Left + TilePad;
        // Keep title + subtitle clear of the top-right gear + grab-handle
        // affordances. GearRect.Left = tile.Right - AffordanceInset -
        // AffordanceSize - 6 - AffordanceSize (i.e. Right - 68); reserve that zone
        // plus a small gap from the right edge.
        float affordanceReserve = AffordanceInset + AffordanceSize + 6 + AffordanceSize + 6;
        float maxX = v.Rect.Right - affordanceReserve;
        float baseline = v.Y + 14;
        using var paint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semibold"), 15);

        // The kind title and its device subtitle share ONE title row, so a
        // subtitle never pushes the bar/graph/details down — every tile advances
        // the same 30px and stays aligned. A long subtitle fade-truncates into
        // the card instead of overflowing.
        DrawTextFaded(canvas, text, x, baseline, maxX, font, paint, _theme.TileBackground);
        if (!string.IsNullOrEmpty(subtitle))
        {
            x += font.MeasureText(text) + 8;
            using var subPaint = new SKPaint { Color = _theme.TextSecondary.WithAlpha(150), IsAntialias = true };
            using var subFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 12);
            DrawTextFaded(canvas, subtitle!, x, baseline, maxX, subFont, subPaint, _theme.TileBackground);
        }
        v.Y += 30; // constant, with or without a subtitle
    }

    private void DrawBigValue(SKCanvas canvas, TileVisual v, double value, string suffix)
    {
        if (!v.Settings.ShowBigValue) return;
        float x = v.Rect.Left + TilePad;
        string valStr = Format.Value(value);
        float size = BigValueFontSize(canvas, v, valStr, suffix);
        float baseline = v.Y + size * BigValueBaselineRatio;
        using var paint = new SKPaint { Color = v.ValueColor, IsAntialias = true };
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI Light"), size);
        canvas.DrawText(valStr, x, baseline, font, paint);

        using var suffixPaint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        using var suffixFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semilight"), size * (20f / BigValueMaxFont));
        float valueWidth = font.MeasureText(valStr);
        canvas.DrawText(suffix, x + valueWidth + 6, baseline, suffixFont, suffixPaint);
        v.Y += size * BigValueAdvanceRatio;
    }

    private void DrawBar(SKCanvas canvas, TileVisual v, double fraction)
    {
        if (!v.Settings.ShowUsageBar) return;
        float x = v.Rect.Left + TilePad;
        float w = v.Rect.Width - 2 * TilePad;
        float y = v.Y;
        float h = 10;
        fraction = Math.Max(0, Math.Min(1, fraction));
        using var track = new SKRoundRect(new SKRect(x, y, x + w, y + h), 5);
        using var trackPaint = new SKPaint { Color = TilePalette.Soft(v.Accent), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(track, trackPaint);

        float fillW = Math.Max(0, w * (float)fraction);
        if (fillW > 1)
        {
            using var fill = new SKRoundRect(new SKRect(x, y, x + fillW, y + h), 5);
            using var fillPaint = new SKPaint { Color = v.ValueColor, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(fill, fillPaint);
        }
        v.Y += 22;
    }

    private void DrawSecondary(SKCanvas canvas, TileVisual v, string text)
    {
        if (!v.Settings.ShowSecondaryLine) return;
        v.SecondaryText = text; // placed during Finish()
    }

    private void DrawSparkline(SKCanvas canvas, TileVisual v, IReadOnlyList<(DateTimeOffset, double)> history, bool isBytes,
        IReadOnlyList<(DateTimeOffset, double)>? history2 = null, SKColor accent2 = default, string? legendA = null, string? legendB = null)
    {
        if (!v.Settings.ShowSparkline) return;
        v.HasSparkline = true;
        v.SparkHistory = history;
        v.SparkHistory2 = history2;
        v.Accent2 = accent2;
        v.SparkLegendA = legendA;
        v.SparkLegendB = legendB;
        // Axis max with hysteresis: steps UP immediately when the data needs
        // it, but steps DOWN only when the new target is clearly lower (70% of
        // current) — stops the ladder-rung flapping that made the whole curve
        // pump up and down (the "heartbeat"). Dual graphs scale to BOTH series.
        double dataMax = 0;
        foreach (var s in history)
            if (s.Item2 > dataMax) dataMax = s.Item2;
        if (history2 != null)
            foreach (var s in history2)
                if (s.Item2 > dataMax) dataMax = s.Item2;
        double axis = NextAxisMax(v.Kind, dataMax, isBytes);
        v.SparkRange = (0, axis);
        v.SparkMaxLabel = isBytes ? Format.Rate((ulong)axis, v.Settings.UnitMode, v.Kind) : $"{axis:0}%";
        // Reference line pinned at 90% of the axis on every tile: near the top,
        // never wanders, level across tiles.
        v.SparkTypicalMax = axis * 0.9;
    }

    // Candidate axis ceilings for percentage tiles (pinned 0..100).
    private static readonly double[] PercentSteps = { 10, 20, 25, 50, 75, 100 };
    // Candidate axis ceilings for byte-rate tiles (1/2/5 × 10^n ladder).
    // Extends to 20 GB/s so NVMe-class disk throughput doesn't clamp the axis.
    private static readonly double[] ByteSteps =
    {
        10 * 1024, 20 * 1024, 50 * 1024,
        100 * 1024, 200 * 1024, 500 * 1024,
        1000 * 1024, 2000 * 1024, 5000 * 1024,
        10L * 1024 * 1024, 20L * 1024 * 1024, 50L * 1024 * 1024,
        100L * 1024 * 1024, 200L * 1024 * 1024, 500L * 1024 * 1024,
        1000L * 1024 * 1024, 2000L * 1024 * 1024, 5000L * 1024 * 1024,
        10000L * 1024 * 1024, 20000L * 1024 * 1024,
    };

    // Per-kind axis ceiling with hysteresis (renderer-owned state; each
    // window's renderer tracks its own copy — a kind lives in one window).
    private readonly Dictionary<TileKind, double> _axisMax = new();

    /// <summary>Axis ceiling for a tile this frame: the nice-ladder rung above
    /// dataMax × 1.2, with hysteresis — jumps up immediately, steps down only
    /// when the new target falls below 70% of the current rung. Prevents the
    /// axis (and with it the whole curve) from flapping between adjacent rungs.
    /// </summary>
    private double NextAxisMax(TileKind kind, double dataMax, bool isBytes)
    {
        var steps = isBytes ? ByteSteps : PercentSteps;
        double floor = isBytes ? 10 * 1024 : 10;
        double ceil = isBytes ? double.PositiveInfinity : 100;
        double target = NiceCeiling(Math.Max(dataMax * 1.2, floor), steps, floor, ceil);
        _axisMax.TryGetValue(kind, out double cur);
        if (cur <= 0 || target > cur || target < cur * 0.7) cur = target;
        _axisMax[kind] = cur;
        return cur;
    }

    /// <summary>Smallest candidate >= raw (and >= floor); for percent, also capped at ceil.</summary>
    private static double NiceCeiling(double raw, double[] steps, double floor, double ceil = double.PositiveInfinity)
    {
        if (raw <= 0) return floor;
        double best = floor;
        foreach (var s in steps)
        {
            if (s >= raw) { best = s; break; }
            best = s;
        }
        if (best < floor) best = floor;
        if (best > ceil) best = ceil;
        return best;
    }

    /// <summary>
    /// Draws the sparkline path into a rect using the tile accent (line + soft
    /// fill). When the tile is in alert state the line uses the alert color.
    /// Exposed so <see cref="TileVisual.Finish"/> can call it after the
    /// remaining-space rect is computed.
    /// </summary>
    public void DrawSparklinePath(SKCanvas canvas, SKRect rect, TileVisual v)
    {
        var history = v.SparkHistory;
        if (history == null || history.Count < 2) return;
        float pad = 4;
        float x0 = rect.Left + pad;
        float y0 = rect.Top + pad;
        float w = rect.Width - pad * 2;
        float h = rect.Height - pad * 2;
        if (w <= 0 || h <= 0) return;

        double range = v.SparkRange.Max - v.SparkRange.Min;
        if (range <= 0) range = 1;

        // Time-mapped X: each sample's position comes from its timestamp within
        // the window (0 = window start, 1 = now), so the curve scrolls smoothly
        // and its shape is stable — no per-frame re-bucketing, no stretching,
        // no oversized remainder bucket stuck at the right edge.
        double winSec = v.SparkWindowSeconds > 0 ? v.SparkWindowSeconds : 60;
        var now = DateTimeOffset.UtcNow;

        SKPoint[] MapPoints(IReadOnlyList<(DateTimeOffset, double)> hist)
        {
            var pts = new SKPoint[hist.Count];
            for (int i = 0; i < hist.Count; i++)
            {
                double t01 = 1.0 + (hist[i].Item1 - now).TotalSeconds / winSec;
                t01 = Math.Max(0, Math.Min(1, t01));
                double norm = (hist[i].Item2 - v.SparkRange.Min) / range;
                norm = Math.Max(0, Math.Min(1, norm));
                pts[i] = new SKPoint(x0 + (float)t01 * w, y0 + h - (float)norm * h);
            }
            return pts;
        }

        var pts = MapPoints(history);
        int m = pts.Length;

        // Smooth path (Catmull-Rom -> cubic Bézier; control points clamped to
        // the plot rect so the curve never overshoots).
        using var path = new SKPath();
        BuildSmoothPath(path, pts, y0, h);

        using var fillPath = new SKPath(path);
        fillPath.LineTo(pts[m - 1].X, y0 + h);
        fillPath.LineTo(pts[0].X, y0 + h);
        fillPath.Close();

        using var fillPaint = new SKPaint
        {
            Color = TilePalette.SparkFill(v.Accent),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        canvas.DrawPath(fillPath, fillPaint);

        // Second series (dual graphs): line only, drawn before the primary
        // line so the accent stays visually on top.
        if (v.SparkHistory2 != null && v.SparkHistory2.Count >= 2)
        {
            using var path2 = new SKPath();
            BuildSmoothPath(path2, MapPoints(v.SparkHistory2), y0, h);
            using var line2Paint = new SKPaint
            {
                Color = v.Accent2,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                IsAntialias = true,
            };
            canvas.DrawPath(path2, line2Paint);
        }

        using var linePaint = new SKPaint
        {
            Color = v.ValueColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true,
        };
        canvas.DrawPath(path, linePaint);

        // Legend (dual graphs): a colored swatch + label per series, top-left.
        if (v.SparkLegendA != null && v.SparkLegendB != null)
        {
            using var legFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 10);
            using var legPaint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
            using var swA = new SKPaint { Color = v.ValueColor, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
            using var swB = new SKPaint { Color = v.Accent2, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
            float lx = x0;
            float ly = y0 + 11;
            canvas.DrawLine(lx, ly - 3, lx + 10, ly - 3, swA);
            canvas.DrawText(v.SparkLegendA, lx + 13, ly, legFont, legPaint);
            lx += 13 + legFont.MeasureText(v.SparkLegendA) + 10;
            canvas.DrawLine(lx, ly - 3, lx + 10, ly - 3, swB);
            canvas.DrawText(v.SparkLegendB, lx + 13, ly, legFont, legPaint);
        }

        // Dashed "typical max" reference line (95th pct, nice-rounded). Sits
        // inside the current axis range; genuine spikes break above it.
        if (v.SparkTypicalMax > v.SparkRange.Min)
        {
            double tnorm = (v.SparkTypicalMax - v.SparkRange.Min) / range;
            tnorm = Math.Max(0, Math.Min(1, tnorm));
            float ty = y0 + h - (float)tnorm * h;
            using var dash = new SKPaint
            {
                Color = _theme.TextSecondary.WithAlpha(90), // ~35% opacity
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new float[] { 4, 3 }, 0),
            };
            canvas.DrawLine(x0, ty, x0 + w, ty, dash);
        }

        // Faint mid gridline (half the axis max), kept subtle and inside the pad.
        float midY = y0 + h / 2;
        using var gridPaint = new SKPaint
        {
            Color = _theme.TileBorder.WithAlpha(70),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true,
        };
        canvas.DrawLine(x0, midY, x0 + w, midY, gridPaint);

        // Max-value axis label in the graph's top-right corner (muted).
        if (!string.IsNullOrEmpty(v.SparkMaxLabel))
        {
            using var lblPaint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
            using var lblFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 10);
            float lw = lblFont.MeasureText(v.SparkMaxLabel);
            canvas.DrawText(v.SparkMaxLabel, x0 + w - lw, y0 + 11, lblFont, lblPaint);
        }
    }

    /// <summary>Builds a Catmull-Rom spline (uniform) through the points as cubic
    /// Béziers. Bézier control points are clamped to the min/max of their adjacent
    /// samples so the curve never overshoots outside [y0, y0+h].</summary>
    private static void BuildSmoothPath(SKPath path, SKPoint[] p, float y0, float h)
    {
        if (p.Length < 2) return;
        path.MoveTo(p[0].X, p[0].Y);
        for (int i = 0; i < p.Length - 1; i++)
        {
            var p0 = p[i == 0 ? 0 : i - 1];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = p[i + 2 < p.Length ? i + 2 : p.Length - 1];

            float c1x = p1.X + (p2.X - p0.X) / 6f;
            float c1y = ClampY(p1.Y + (p2.Y - p0.Y) / 6f, y0, h);
            float c2x = p2.X - (p3.X - p1.X) / 6f;
            float c2y = ClampY(p2.Y - (p3.Y - p1.Y) / 6f, y0, h);

            path.CubicTo(c1x, c1y, c2x, c2y, p2.X, p2.Y);
        }
    }

    private static float ClampY(float y, float y0, float h) => Math.Max(y0, Math.Min(y0 + h, y));

    // ---- settings pane ----

    /// <summary>
    /// Computes the on-screen rectangle of a tile's settings pane (a rounded
    /// card that drops below the gear). Anchored to the gear and clamped to the
    /// host <paramref name="client"/> area, so it can extend beyond the tile
    /// (popover) but never off-window. If it can't fit below, it flips above.
    /// </summary>
    public SKRect? SettingsPaneRect(SKRect tile, SKRect client)
    {
        var gear = GearRect(tile);
        float x = gear.Left;
        if (x + PaneWidth > client.Right - 8) x = client.Right - 8 - PaneWidth;
        if (x < client.Left + 8) x = client.Left + 8;

        float below = gear.Bottom + 8;
        float above = gear.Top - 8 - PaneHeight;
        float y = below;
        if (below + PaneHeight > client.Bottom - 8)
        {
            y = above;
            if (y < client.Top + 8) y = client.Top + 8;
        }
        if (y + PaneHeight > client.Bottom - 8) return null; // genuinely no room
        return new SKRect(x, y, x + PaneWidth, y + PaneHeight);
    }

    /// <summary>
    /// Like <see cref="SettingsPaneRect(SKRect, SKRect)"/> but reserves extra
    /// vertical space (used by multi-instance tiles for the device row + remove
    /// button). Falls back to the standard placement when the taller pane cannot
    /// fit, so a crowded window still shows the base pane.
    /// </summary>
    public SKRect? SettingsPaneRect(SKRect tile, SKRect client, float extraHeight)
    {
        float height = PaneHeight + extraHeight;
        var gear = GearRect(tile);
        float x = gear.Left;
        if (x + PaneWidth > client.Right - 8) x = client.Right - 8 - PaneWidth;
        if (x < client.Left + 8) x = client.Left + 8;

        float below = gear.Bottom + 8;
        float above = gear.Top - 8 - height;
        float y = below;
        if (below + height > client.Bottom - 8)
        {
            y = above;
            if (y < client.Top + 8) y = client.Top + 8;
        }
        if (y + height > client.Bottom - 8) return null;
        return new SKRect(x, y, x + PaneWidth, y + height);
    }

    /// <summary>
    /// All on-screen hit rects of an open settings pane, so the host can route
    /// clicks precisely without re-deriving geometry.
    /// </summary>
    public sealed class PaneLayout
    {
        public SKRect Pane { get; init; }
        public IReadOnlyList<SKRect> Toggles { get; init; } = Array.Empty<SKRect>();
        public IReadOnlyList<SKRect> UnitSegments { get; init; } = Array.Empty<SKRect>();
        public IReadOnlyList<SKRect> Swatches { get; init; } = Array.Empty<SKRect>();
        public SKRect Custom { get; init; }
        public SKRect Reset { get; init; }
        public SKRect Close { get; init; }

        // ---- multi-instance device members (additive; default empty) ----
        /// <summary>The "Device: <name>" selector row (multi-instance tiles only).</summary>
        public SKRect DeviceRow { get; init; } = SKRect.Empty;
        /// <summary>The open dropdown sub-card rect (empty when closed).</summary>
        public SKRect DeviceDropdown { get; init; } = SKRect.Empty;
        /// <summary>Per visible dropdown item hit rects (for hit-testing).</summary>
        public IReadOnlyList<SKRect> DeviceDropdownItems { get; init; } = Array.Empty<SKRect>();
        /// <summary>Up / down overflow-arrow hit rects (empty when list fits).</summary>
        public SKRect DeviceDropdownUp { get; init; } = SKRect.Empty;
        public SKRect DeviceDropdownDown { get; init; } = SKRect.Empty;
        /// <summary>The danger-tinted "Remove tile" button rect (multi-instance tiles only).</summary>
        public SKRect RemoveTile { get; init; } = SKRect.Empty;
    }

    /// <summary>
    /// Computes the pane's hit rects for a tile without drawing. Returns null
    /// when the pane cannot be placed in the client area.
    /// </summary>
    public PaneLayout? ComputePaneLayout(SKRect tile, SKRect client, TileVisual v)
    {
        var pane = SettingsPaneRect(tile, client);
        if (pane is null) return null;
        var p = pane.Value;

        float x = p.Left + PanePad;
        float right = p.Right - PanePad;
        float y = p.Top + PanePad + PaneHeaderGap;

        var toggles = new List<SKRect>(5);
        for (int i = 0; i < 5; i++)
        {
            toggles.Add(new SKRect(x, y, right, y + PaneToggleH));
            y += PaneToggleH;
        }

        y += 4;
        // Units segmented control (Auto / Bits / Bytes) between toggles and divider.
        float unitLabelH = PaneAccentLabelH;
        var unitSegs = SegmentRects(x, right, y + unitLabelH, PaneToggleH);
        y += unitLabelH + PaneToggleH;

        y += 10; // divider gap
        y += PaneAccentLabelH;

        var swatches = new List<SKRect>(12);
        var palette = SwatchPalette();
        int swatchRows = (int)Math.Ceiling((double)palette.Count / SwatchPerRow);
        for (int i = 0; i < palette.Count; i++)
        {
            int col = i % SwatchPerRow;
            int row = i / SwatchPerRow;
            float sx = x + col * (SwatchW + SwatchGap);
            float sy = y + row * (SwatchH + SwatchGap);
            swatches.Add(new SKRect(sx, sy, sx + SwatchW, sy + SwatchH));
        }
        y += swatchRows * (SwatchH + SwatchGap) + 6;

        float midX = (x + right) / 2;
        var custom = new SKRect(x, y, midX - 4, y + PaneButtonH);
        var reset = new SKRect(midX + 4, y, right, y + PaneButtonH);

        var close = new SKRect(p.Right - 28, p.Top + 6, p.Right - 8, p.Top + 26);

        return new PaneLayout
        {
            Pane = p,
            Toggles = toggles,
            UnitSegments = unitSegs,
            Swatches = swatches,
            Custom = custom,
            Reset = reset,
            Close = close,
        };
    }

    /// <summary>
    /// Draws the open settings pane for a tile: a rounded card with the five
    /// element toggles and a curated accent color picker (swatches + Custom…
    /// + Reset). Uses a precomputed <see cref="PaneLayout"/> for hit rects.
    /// </summary>
    public void DrawSettingsPane(SKCanvas canvas, PaneLayout layout, TileVisual v, bool hoverClose)
    {
        var pane = layout.Pane;
        using var round = new SKRoundRect(pane, 14);
        using var bg = new SKPaint { Color = _theme.PaneBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(round, bg);
        using var border = new SKPaint { Color = _theme.TileBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawRoundRect(round, border);

        float x = pane.Left + PanePad;
        float right = pane.Right - PanePad;
        float y = pane.Top + PanePad;

        using var headerPaint = new SKPaint { Color = _theme.TextPrimary, IsAntialias = true };
        using var headerFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semibold"), 13);
        canvas.DrawText("Customize", x, y + 12, headerFont, headerPaint);
        y += PaneHeaderGap;

        DrawCloseGlyph(canvas, layout.Close, hoverClose);

        var labels = new[] { ("Title", v.Settings.ShowTitle), ("Usage %", v.Settings.ShowBigValue),
            ("Bar", v.Settings.ShowUsageBar), ("Graph", v.Settings.ShowSparkline), ("Details", v.Settings.ShowSecondaryLine) };
        for (int i = 0; i < labels.Length; i++)
        {
            DrawToggleRow(canvas, layout.Toggles[i], labels[i].Item1, labels[i].Item2, v.Accent);
        }
        y = layout.Toggles[^1].Bottom + 4;

        // Units segmented control (Auto / Bits / Bytes).
        using var unitLabelPaint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        using var unitLabelFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 12);
        canvas.DrawText("Units", x, y + 11, unitLabelFont, unitLabelPaint);
        string[] unitLabels = { "Auto", "Bits", "Bytes" };
        int unitIdx = (int)v.Settings.UnitMode;
        DrawSegments(canvas, layout.UnitSegments, unitLabels, unitIdx, v.Accent);
        y = layout.UnitSegments[^1].Bottom + 4;

        using var div = new SKPaint { Color = _theme.TileBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(x, y, right, y, div);
        y += 10;

        using var subPaint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        using var subFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 12);
        canvas.DrawText("Accent", x, y + 11, subFont, subPaint);
        y += PaneAccentLabelH;

        var palette = SwatchPalette();
        for (int i = 0; i < palette.Count; i++)
        {
            bool selected = v.Settings.AccentColor is null
                ? palette[i].Equals(TilePalette.DefaultFor(v.Kind))
                : palette[i].Equals(v.Settings.AccentColor.Value);
            DrawSwatch(canvas, layout.Swatches[i], palette[i], selected);
        }
        y = layout.Swatches[^1].Bottom + 6;

        DrawButton(canvas, layout.Custom, "Custom…", v.Accent, false);
        DrawButton(canvas, layout.Reset, "Reset to default", v.Accent, true);
    }

    // ---- global settings pane ----

    public const float GlobalPaneWidth = 400;
    // Two-column layout height: header + (4 toggles + stepper) in the taller
    // left column + full-width Tiles section. Deliberately < 284px so the pane
    // fits a 300px-tall window (client.Height - 16).
    public const float GlobalPaneHeight = 300;

    /// <summary>All on-screen hit rects of the open global settings pane.</summary>
    public sealed class GlobalPaneLayout
    {
        public SKRect Pane { get; init; }
        public SKRect Close { get; init; }
        public SKRect LaunchToggle { get; init; }
        public SKRect KioskToggle { get; init; }
        public SKRect AlwaysOnTopToggle { get; init; }
        public IReadOnlyList<SKRect> ThemeSegments { get; init; } = Array.Empty<SKRect>();
        public SKRect ThresholdToggle { get; init; }
        public SKRect ThresholdMinus { get; init; }
        public SKRect ThresholdValue { get; init; }
        public SKRect ThresholdPlus { get; init; }
        // Graph span segmented control {30s, 1m, 5m, 10m}.
        public IReadOnlyList<SKRect> GraphSpanSegments { get; init; } = Array.Empty<SKRect>();
        // Value precision segmented control {0, 1, 2} decimals.
        public IReadOnlyList<SKRect> DecimalsSegments { get; init; } = Array.Empty<SKRect>();
        // One hit rect per tile-kind chip in the "Tiles" section, in canonical
        // TileKind order (Cpu, Ram, Gpu, Disk, Network).
        public IReadOnlyList<SKRect> TileChips { get; init; } = Array.Empty<SKRect>();

        // ---- instance-aware Tiles section members (additive; default empty) ----
        /// <summary>Management rows for Disk/GPU/Network: kind label + count + ＋ Add (full-row hit area).</summary>
        public IReadOnlyList<SKRect> AddRows { get; init; } = Array.Empty<SKRect>();
        /// <summary>The "＋ Add" button rects (one per AddRows entry).</summary>
        public IReadOnlyList<SKRect> AddButtons { get; init; } = Array.Empty<SKRect>();
        /// <summary>The open device-picker sub-card rect (empty when no picker open).</summary>
        public SKRect Picker { get; init; } = SKRect.Empty;
        /// <summary>Per visible picker item hit rects.</summary>
        public IReadOnlyList<SKRect> PickerItems { get; init; } = Array.Empty<SKRect>();
        /// <summary>Picker up / down overflow-arrow hit rects (empty when list fits).</summary>
        public SKRect PickerUp { get; init; } = SKRect.Empty;
        public SKRect PickerDown { get; init; } = SKRect.Empty;
        /// <summary>Picker close (×) hit rect.</summary>
        public SKRect PickerClose { get; init; } = SKRect.Empty;
        /// <summary>Picker empty-state text hit rect (when no devices remain).</summary>
        public SKRect PickerEmpty { get; init; } = SKRect.Empty;
    }

    /// <summary>
    /// Computes the global pane's hit rects. The pane is centered in the client
    /// area (clamped on-screen) and uses a TWO-COLUMN layout so it stays short
    /// enough to open on small windows (kiosk 800x480, floating ~420x300).
    /// Returns null only if the window is impossibly small.
    ///
    /// Left column: Launch at startup, Kiosk mode, Always-on-top, Threshold
    /// alert + stepper. Right column: Units, Theme. The "Tiles" enable/disable
    /// chips span the full width at the bottom. All offsets are shared with the
    /// draw routine via the <see cref="GlobalPaneLayout"/> rects (no parallel
    /// geometry), so hit-claim, routing and drawing never drift apart.
    /// </summary>
    public GlobalPaneLayout? ComputeGlobalPaneLayout(SKRect client)
    {
        float width = Math.Min(GlobalPaneWidth, client.Width - 16);
        if (width < 280) width = 280;
        float height = GlobalPaneHeight;
        float x = client.Left + (client.Width - width) / 2;
        float y = client.Top + (client.Height - height) / 2 - 6;
        if (x < client.Left + 8) x = client.Left + 8;
        if (y < client.Top + 8) y = client.Top + 8;
        if (x + width > client.Right - 8) x = client.Right - 8 - width;
        if (y + height > client.Bottom - 8) y = client.Bottom - 8 - height;
        if (width > client.Width - 16 || height > client.Height - 16) return null;

        var p = new SKRect(x, y, x + width, y + height);
        float pad = 14;
        float lx = p.Left + pad;
        float rx = p.Right - pad;
        float colGap = 16;
        float leftW = (rx - lx - colGap) / 2;
        float rightX = lx + leftW + colGap;

        var close = new SKRect(p.Right - 28, p.Top + 6, p.Right - 8, p.Top + 26);

        const float rowH = 30;   // switch / stepper row height
        const float segH = 26;    // segmented control height
        const float segLabelH = 16;
        const float segRowH = segLabelH + 4 + segH; // label + gap + control
        const float toggleGap = 3;

        // ---- left column ----
        float ly = p.Top + pad + 20; // below the "Settings" header
        var launchToggle = new SKRect(lx, ly, lx + leftW, ly + rowH); ly += rowH + toggleGap;
        var kioskToggle = new SKRect(lx, ly, lx + leftW, ly + rowH); ly += rowH + toggleGap;
        var alwaysOnTopToggle = new SKRect(lx, ly, lx + leftW, ly + rowH); ly += rowH + toggleGap;
        var thresholdToggle = new SKRect(lx, ly, lx + leftW, ly + rowH); ly += rowH + toggleGap;

        // Threshold stepper: STACKED within the left column — "Alert at" label
        // on its own line, the [-] [value] [+] group right-aligned on the line
        // below (mirrors the right column's segmented rows). This makes overlap
        // with the label impossible at any column width.
        float minusW = 26, valW = 42, sgap = 6;
        float plusX = lx + leftW - minusW;
        float valX = plusX - sgap - valW;
        float minusX = valX - sgap - minusW;
        ly += segLabelH + 4; // label line (no control on it)
        var thresholdMinus = new SKRect(minusX, ly, minusX + minusW, ly + rowH);
        var thresholdValue = new SKRect(valX, ly, valX + valW, ly + rowH);
        var thresholdPlus = new SKRect(plusX, ly, plusX + minusW, ly + rowH);
        float leftBottom = ly + rowH;

        // ---- right column ----
        float ry = p.Top + pad + 20;
        var themeSegs = SegmentRects(rightX, rx, ry + segLabelH, segH); ry += segRowH + toggleGap;
        var graphSpanSegs = SegmentRects(rightX, rx, ry + segLabelH, segH, 4); ry += segRowH + toggleGap;
        var decimalsSegs = SegmentRects(rightX, rx, ry + segLabelH, segH); ry += segRowH + toggleGap;
        float rightBottom = ry;

        // ---- Tiles section: full width below both columns ----
        float tilesTop = Math.Max(leftBottom, rightBottom) + 12;
        float chipY = tilesTop + 16 + 4; // header line + gap
        const float chipH = 24;
        var chipKinds = new[] { TileKind.Cpu, TileKind.Ram, TileKind.Gpu, TileKind.Disk, TileKind.Network };
        int n = chipKinds.Length;
        float chipGap = 6;
        float chipW = (rx - lx - chipGap * (n - 1)) / n;
        var chips = new List<SKRect>(n);
        for (int i = 0; i < n; i++)
            chips.Add(new SKRect(lx + i * (chipW + chipGap), chipY, lx + i * (chipW + chipGap) + chipW, chipY + chipH));

        return new GlobalPaneLayout
        {
            Pane = p,
            Close = close,
            LaunchToggle = launchToggle,
            KioskToggle = kioskToggle,
            AlwaysOnTopToggle = alwaysOnTopToggle,
            ThemeSegments = themeSegs,
            ThresholdToggle = thresholdToggle,
            ThresholdMinus = thresholdMinus,
            ThresholdValue = thresholdValue,
            ThresholdPlus = thresholdPlus,
            GraphSpanSegments = graphSpanSegs,
            DecimalsSegments = decimalsSegs,
            TileChips = chips,
        };
    }

    private static IReadOnlyList<SKRect> SegmentRects(float lx, float rx, float y, float rowH, int n = 3)
    {
        float gap = 6;
        float w = (rx - lx - gap * (n - 1)) / n;
        var list = new List<SKRect>(n);
        for (int i = 0; i < n; i++)
            list.Add(new SKRect(lx + i * (w + gap), y, lx + i * (w + gap) + w, y + rowH));
        return list;
    }

    /// <summary>
    /// Draws the global settings pane. Reads live state from <paramref name="config"/>
    /// and the active theme. Two-column layout: left column holds the toggles +
    /// threshold stepper, right column holds the Units/Theme segmented controls,
    /// and the Tiles chips span the full width at the bottom. All hit rects come
    /// from <paramref name="layout"/>, so drawing and hit-testing share one source.
    /// </summary>
    public void DrawGlobalPane(SKCanvas canvas, GlobalPaneLayout layout, AppConfig config, Theme currentTheme, bool hoverClose, int hoverRow)
    {
        var p = layout.Pane;
        using var round = new SKRoundRect(p, 14);
        using var bg = new SKPaint { Color = _theme.PaneBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(round, bg);
        using var border = new SKPaint { Color = _theme.TileBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawRoundRect(round, border);

        float pad = 14;
        float lx = p.Left + pad;
        float rx = p.Right - pad;

        using var headerPaint = new SKPaint { Color = _theme.TextPrimary, IsAntialias = true };
        using var headerFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semibold"), 15);
        canvas.DrawText("Settings", lx, p.Top + pad + 14, headerFont, headerPaint);
        DrawCloseGlyph(canvas, layout.Close, hoverClose);

        const float rowH = 30;
        const float segLabelH = 16;

        // ---- left column: toggles + threshold stepper ----
        DrawRowLabel(canvas, layout.LaunchToggle.Left, layout.LaunchToggle.Top, rowH, "Launch at startup");
        DrawSwitch(canvas, layout.LaunchToggle, config.LaunchAtStartup, _theme.Accent);
        DrawRowLabel(canvas, layout.KioskToggle.Left, layout.KioskToggle.Top, rowH, "Kiosk mode");
        DrawSwitch(canvas, layout.KioskToggle, config.KioskMode, _theme.Accent);
        DrawRowLabel(canvas, layout.AlwaysOnTopToggle.Left, layout.AlwaysOnTopToggle.Top, rowH, "Always on top");
        DrawSwitch(canvas, layout.AlwaysOnTopToggle, config.AlwaysOnTop, _theme.Accent);
        DrawRowLabel(canvas, layout.ThresholdToggle.Left, layout.ThresholdToggle.Top, rowH, "Threshold alert");
        DrawSwitch(canvas, layout.ThresholdToggle, config.ThresholdEnabled, _theme.Accent);

        // Threshold % stepper — label on its own line ABOVE the right-aligned
        // [-] [value] [+] group (stacked, never beside it).
        DrawRowLabel(canvas, lx, layout.ThresholdMinus.Top - segLabelH, segLabelH, "Alert at");
        DrawStepper(canvas, layout.ThresholdMinus, layout.ThresholdValue, layout.ThresholdPlus,
            $"{config.ThresholdPercent:0}%", _theme.Accent);

        // ---- right column: Theme segmented ----
        DrawRowLabel(canvas, layout.ThemeSegments[0].Left, layout.ThemeSegments[0].Top - segLabelH, segLabelH, "Theme");
        string[] themeLabels = { "Midnight", "Obsidian", "Daybreak" };
        int themeIdx = IndexOfTheme(currentTheme.Name);
        DrawSegments(canvas, layout.ThemeSegments, themeLabels, themeIdx, _theme.Accent);

        // Graph span {30s, 1m, 5m, 10m} — segmented, right column.
        DrawRowLabel(canvas, layout.GraphSpanSegments[0].Left, layout.GraphSpanSegments[0].Top - segLabelH, segLabelH, "Graph span");
        string[] spanLabels = { "30s", "1m", "5m", "10m" };
        int spanIdx = GraphSpanIndex(config.GraphWindowSeconds);
        DrawSegments(canvas, layout.GraphSpanSegments, spanLabels, spanIdx, _theme.Accent);

        // Decimals {0, 1, 2} — segmented, right column.
        DrawRowLabel(canvas, layout.DecimalsSegments[0].Left, layout.DecimalsSegments[0].Top - segLabelH, segLabelH, "Decimals");
        string[] decLabels = { "0", "1", "2" };
        DrawSegments(canvas, layout.DecimalsSegments, decLabels, config.ValueDecimals, _theme.Accent);

        // ---- Tiles section: full width ----
        using var secPaint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        using var secFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semibold"), 12);
        float tilesHeaderY = layout.TileChips[0].Top - 4 - 13;
        canvas.DrawText("Tiles", lx, tilesHeaderY + 13, secFont, secPaint);
        var chipKinds = new[] { TileKind.Cpu, TileKind.Ram, TileKind.Gpu, TileKind.Disk, TileKind.Network };
        for (int i = 0; i < chipKinds.Length; i++)
            DrawChip(canvas, layout.TileChips[i], chipKinds[i], config.Tile(chipKinds[i]).Enabled);
    }

    private static int IndexOfTheme(string name)
    {
        var all = Theme.All;
        for (int i = 0; i < all.Count; i++)
            if (string.Equals(all[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    /// <summary>Maps a GraphWindowSeconds value to the {30s,1m,5m,10m} index.</summary>
    public static int GraphSpanIndex(int seconds)
    {
        if (seconds <= 30) return 0;
        if (seconds <= 60) return 1;
        if (seconds <= 300) return 2;
        return 3;
    }

    /// <summary>Maps a {30s,1m,5m,10m} index back to seconds.</summary>
    public static int GraphSpanSeconds(int idx) => idx switch
    {
        0 => 30,
        1 => 60,
        2 => 300,
        _ => 600,
    };

    private void DrawRowLabel(SKCanvas canvas, float x, float y, float rowH, string text)
    {
        using var paint = new SKPaint { Color = _theme.TextPrimary, IsAntialias = true };
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 13);
        // Vertically center the label within the row height (baseline ~ 0.72 * h).
        canvas.DrawText(text, x, y + rowH * 0.72f, font, paint);
    }

    private void DrawSwitch(SKCanvas canvas, SKRect row, bool on, SKColor accent)
    {
        float sw = 34, sh = 18;
        float sx = row.Right - sw;
        float sy = row.MidY - sh / 2;
        using var trackR = new SKRoundRect(new SKRect(sx, sy, sx + sw, sy + sh), sh / 2);
        using var trackP = new SKPaint { Color = on ? accent : _theme.TileBorder, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(trackR, trackP);
        float knob = sh - 4;
        float kx = on ? sx + sw - knob - 2 : sx + 2;
        using var knobP = new SKPaint { Color = _theme.TextPrimary, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(kx + knob / 2, sy + sh / 2, knob / 2, knobP);
    }

    private void DrawSegments(SKCanvas canvas, IReadOnlyList<SKRect> segs, string[] labels, int selected, SKColor accent)
    {
        for (int i = 0; i < segs.Count && i < labels.Length; i++)
        {
            bool on = i == selected;
            using var rr = new SKRoundRect(segs[i], 8);
            using var p = new SKPaint { Color = on ? TilePalette.Soft(accent) : _theme.TileBorder, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(rr, p);
            using var tPaint = new SKPaint { Color = on ? accent : _theme.TextSecondary, IsAntialias = true };
            using var tFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semibold"), 12);
            float tw = tFont.MeasureText(labels[i]);
            canvas.DrawText(labels[i], segs[i].MidX - tw / 2, segs[i].MidY + 4, tFont, tPaint);
        }
    }

    private void DrawStepper(SKCanvas canvas, SKRect minus, SKRect value, SKRect plus, string valueText, SKColor accent)
    {
        DrawStepperMinus(canvas, minus, accent);
        DrawStepperButton(canvas, plus, "+", accent);
        // Fixed-width value slot (display only).
        using var rr = new SKRoundRect(value, 8);
        using var p = new SKPaint { Color = TilePalette.Soft(accent), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(rr, p);
        using var tPaint = new SKPaint { Color = accent, IsAntialias = true };
        using var tFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semibold"), 14);
        float tw = tFont.MeasureText(valueText);
        canvas.DrawText(valueText, value.MidX - tw / 2, value.MidY + 5, tFont, tPaint);
    }

    private void DrawStepperButton(SKCanvas canvas, SKRect r, string glyph, SKColor accent)
    {
        using var rr = new SKRoundRect(r, 8);
        using var p = new SKPaint { Color = TilePalette.Soft(accent), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(rr, p);
        using var tPaint = new SKPaint { Color = accent, IsAntialias = true };
        using var tFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semibold"), 16);
        float tw = tFont.MeasureText(glyph);
        canvas.DrawText(glyph, r.MidX - tw / 2, r.MidY + 6, tFont, tPaint);
    }

    /// <summary>
    /// Draws the stepper's minus button with a STROKED horizontal line instead
    /// of the "−" (U+2212) glyph. The minus sign is not guaranteed to be present
    /// in Segoe UI at every rendered size, whereas a stroked line always is.
    /// </summary>
    private void DrawStepperMinus(SKCanvas canvas, SKRect r, SKColor accent)
    {
        using var rr = new SKRoundRect(r, 8);
        using var p = new SKPaint { Color = TilePalette.Soft(accent), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(rr, p);
        using var tPaint = new SKPaint
        {
            Color = accent,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.8f,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
        };
        float m = Math.Min(r.Width, r.Height) * 0.22f;
        canvas.DrawLine(r.MidX - m, r.MidY, r.MidX + m, r.MidY, tPaint);
    }

    /// <summary>
    /// Draws a tile enable/disable chip: a rounded pill showing the kind's short
    /// name. Enabled = filled with the kind's accent (dark text for contrast);
    /// disabled = dim outline with muted text.
    /// </summary>
    private void DrawChip(SKCanvas canvas, SKRect r, TileKind kind, bool enabled)
    {
        var accent = TilePalette.DefaultFor(kind);
        using var rr = new SKRoundRect(r, 13);
        if (enabled)
        {
            using var p = new SKPaint { Color = accent, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(rr, p);
        }
        else
        {
            using var p = new SKPaint { Color = _theme.TileBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawRoundRect(rr, p);
        }

        string label = kind switch
        {
            TileKind.Cpu => "CPU",
            TileKind.Ram => "RAM",
            TileKind.Gpu => "GPU",
            TileKind.Disk => "DISK",
            TileKind.Network => "NET",
            _ => kind.ToString(),
        };
        using var tPaint = new SKPaint
        {
            Color = enabled ? new SKColor(0x12, 0x16, 0x1C) : _theme.TextSecondary,
            IsAntialias = true,
        };
        using var tFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semibold"), 11);
        float tw = tFont.MeasureText(label);
        canvas.DrawText(label, r.MidX - tw / 2, r.MidY + 4, tFont, tPaint);
    }

    /// <summary>
    /// The curated swatch palette: the four per-kind defaults plus eight
    /// well-chosen hues that stay legible on the dark card.
    /// </summary>
    public static IReadOnlyList<SKColor> SwatchPalette() => new[]
    {
        TilePalette.CpuDefault,  TilePalette.RamDefault,  TilePalette.GpuDefault,  TilePalette.DiskDefault,  TilePalette.NetworkDefault,
        new SKColor(0x9B, 0x8C, 0xFF), // violet
        new SKColor(0x4D, 0xD0, 0xE1), // cyan
        new SKColor(0xF4, 0xD0, 0x35), // amber
        new SKColor(0xE5, 0x6B, 0x6B), // red
        new SKColor(0x80, 0xC9, 0x7A), // lime
        new SKColor(0xC0, 0xCA, 0xD4), // slate
        new SKColor(0xFF, 0x7A, 0x59), // coral
        new SKColor(0x6C, 0xD4, 0x9B), // mint
    };

    private void DrawToggleRow(SKCanvas canvas, SKRect row, string label, bool on, SKColor accent)
    {
        using var labelPaint = new SKPaint { Color = _theme.TextPrimary, IsAntialias = true };
        using var labelFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 13);
        canvas.DrawText(label, row.Left, row.Top + 15, labelFont, labelPaint);

        float sw = 34, sh = 18;
        float sx = row.Right - sw;
        float sy = row.MidY - sh / 2;
        using var trackR = new SKRoundRect(new SKRect(sx, sy, sx + sw, sy + sh), sh / 2);
        using var trackP = new SKPaint { Color = on ? accent : _theme.TileBorder, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(trackR, trackP);
        float knob = sh - 4;
        float kx = on ? sx + sw - knob - 2 : sx + 2;
        using var knobP = new SKPaint { Color = _theme.TextPrimary, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(kx + knob / 2, sy + sh / 2, knob / 2, knobP);
    }

    private void DrawSwatch(SKCanvas canvas, SKRect r, SKColor c, bool selected)
    {
        using var rr = new SKRoundRect(r, 6);
        using var p = new SKPaint { Color = c, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(rr, p);
        if (selected)
        {
            using var sel = new SKPaint { Color = _theme.TextPrimary, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
            canvas.DrawRoundRect(new SKRoundRect(SKRect.Inflate(r, -2, -2), 5), sel);
        }
    }

    private void DrawButton(SKCanvas canvas, SKRect r, string text, SKColor accent, bool subtle)
    {
        using var rr = new SKRoundRect(r, 8);
        using var p = new SKPaint
        {
            Color = subtle ? _theme.TileBorder : TilePalette.Soft(accent),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        canvas.DrawRoundRect(rr, p);
        using var tPaint = new SKPaint { Color = subtle ? _theme.TextPrimary : accent, IsAntialias = true };
        using var tFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semibold"), 12);
        float tw = tFont.MeasureText(text);
        canvas.DrawText(text, r.MidX - tw / 2, r.MidY + 4, tFont, tPaint);
    }

    private void DrawCloseGlyph(SKCanvas canvas, SKRect r, bool hover)
    {
        using var p = new SKPaint { Color = hover ? _theme.TextPrimary : _theme.TextSecondary, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        float m = 7;
        canvas.DrawLine(r.Left + m, r.Top + m, r.Right - m, r.Bottom - m, p);
        canvas.DrawLine(r.Right - m, r.Top + m, r.Left + m, r.Bottom - m, p);
    }
}

/// <summary>
/// Mutable per-draw layout cursor for a tile. Tracks the running vertical
/// position, the resolved accent, the tile settings, and which optional
/// elements are present so the sparkline can fill the remaining space and the
/// secondary line can sit just above it. When <see cref="Alert"/> is set, the
/// alert color replaces the accent for the bar fill, big value, and sparkline
/// stroke (threshold coloring).
/// </summary>
public sealed class TileVisual
{
    public TileKind Kind { get; }
    public TileSettings Settings { get; }
    public SKColor Accent { get; }
    public SKRect Rect { get; set; }
    public float Y { get; set; }
    public bool HasSparkline { get; set; }
    public string? SecondaryText { get; set; }
    public IReadOnlyList<(DateTimeOffset, double)>? SparkHistory { get; set; }
    public (double Min, double Max) SparkRange { get; set; }
    /// <summary>Typical-max reference value (95th pct, nice-rounded), drawn as a dashed line. Hidden with ShowSparkline.</summary>
    public double SparkTypicalMax { get; set; }
    /// <summary>Human-readable top-of-axis value (e.g. "25%" or "1.2 MB/s"), drawn inside the graph. Hidden with ShowSparkline.</summary>
    public string? SparkMaxLabel { get; set; }
    /// <summary>Graph time window in seconds — the renderer time-maps the sparkline's X axis with it.</summary>
    public double SparkWindowSeconds { get; set; } = 60;
    /// <summary>Optional second series for dual graphs (disk read / net up). Null = single-series graph.</summary>
    public IReadOnlyList<(DateTimeOffset, double)>? SparkHistory2 { get; set; }
    /// <summary>Color of the second series line.</summary>
    public SKColor Accent2 { get; set; }
    /// <summary>Legend label for the primary series (e.g. "W", "↓"). Null = no legend.</summary>
    public string? SparkLegendA { get; set; }
    /// <summary>Legend label for the second series (e.g. "R", "↑").</summary>
    public string? SparkLegendB { get; set; }
    public bool Alert { get; }
    private readonly Theme _theme;

    public TileVisual(TileKind kind, TileSettings settings, SKColor accent, Theme theme, bool alert)
    {
        Kind = kind;
        Settings = settings;
        Accent = accent;
        _theme = theme;
        Alert = alert;
    }

    /// <summary>The color used for alert-prone elements: alert red when active, else the tile accent.</summary>
    public SKColor ValueColor => Alert ? TileRenderer.AlertColor : Accent;

    public void Finish(SKCanvas canvas, TileRenderer renderer)
    {
        float pad = TileRenderer.TilePad;
        float bottom = Rect.Bottom - pad;
        float maxX = Rect.Right - pad;

        // Spacing tokens (tile body). The details line sits in the content flow —
        // clear of the bar above and the graph below — and the graph fills ONLY
        // the space genuinely remaining beneath it. Both degrade gracefully as the
        // tile shortens: the graph drops out first (below GraphFloor), then the
        // details line hides when it can no longer fit vertically — each returns
        // once the tile is large enough again. Long text fade-truncates into the
        // card rather than overflowing.
        const float DetailsFontSize = 14f;
        const float DetailsLineH = 18f;     // vertical allowance for the details row
        const float GapAboveDetails = 6f;   // bar -> details text
        const float GapBelowDetails = 6f;   // details text -> graph
        const float GraphFloor = 40f;       // hide the graph below this height

        float availAfterBar = bottom - Y;

        if (HasSparkline)
        {
            // Details only if it fits vertically; graph only if enough room remains
            // beneath it. Details outlives the graph on a shrinking tile.
            bool showDetails = SecondaryText != null && availAfterBar >= GapAboveDetails + DetailsLineH;
            float graphTop;
            if (showDetails)
            {
                // Baseline = content bottom (Y) + gap + font ascent (~font size).
                float detailsBaseline = Y + GapAboveDetails + DetailsFontSize;
                using (var p = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true })
                using (var f = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), DetailsFontSize))
                    renderer.DrawTextFaded(canvas, SecondaryText!, Rect.Left + pad, detailsBaseline, maxX, f, p, _theme.TileBackground);
                graphTop = Y + GapAboveDetails + DetailsLineH + GapBelowDetails;
            }
            else
            {
                graphTop = Y + GapAboveDetails;
            }

            if (bottom - graphTop >= GraphFloor)
            {
                var r = new SKRect(Rect.Left + pad, graphTop, Rect.Right - pad, bottom);
                renderer.DrawSparklinePath(canvas, r, this);
            }
        }
        else if (SecondaryText != null && availAfterBar >= DetailsLineH)
        {
            // No graph: details pinned to the bottom only when it fits vertically.
            using var p = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
            using var f = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), DetailsFontSize);
            renderer.DrawTextFaded(canvas, SecondaryText, Rect.Left + pad, bottom, maxX, f, p, _theme.TileBackground);
        }
    }
}
