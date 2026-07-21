using SkiaSharp;

namespace PCGauger.Rendering;

/// <summary>
/// A named visual preset. The default ("Midnight") is the original dark palette;
/// "Obsidian" is an OLED true-black variant; "Daybreak" is a legible light theme.
/// Each defines every color the renderer/footer/panes need, including a footer
/// band shade distinct from the window background.
/// </summary>
public sealed class Theme
{
    public string Name { get; }

    public SKColor Background { get; }
    public SKColor TileBackground { get; }
    public SKColor TileBorder { get; }
    public SKColor TextPrimary { get; }
    public SKColor TextSecondary { get; }
    public SKColor Accent { get; }
    public SKColor AccentSoft { get; }
    public SKColor Sparkline { get; }
    /// <summary>Footer status-bar band shade, distinct from the window background.</summary>
    public SKColor FooterBand { get; }
    /// <summary>Settings-pane card background (slightly raised over the tile bg).</summary>
    public SKColor PaneBackground { get; }

    // Cached paints: theme colors are immutable, so these are created once and
    // reused every frame. Callers must NOT dispose them (shared with all tiles).
    private SKPaint? _backgroundPaint;
    private SKPaint? _tilePaint;
    private SKPaint? _tileBorderPaint;
    public SKPaint BackgroundPaint() => _backgroundPaint ??= new SKPaint { Color = Background, Style = SKPaintStyle.Fill };
    public SKPaint TilePaint() => _tilePaint ??= new SKPaint { Color = TileBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
    public SKPaint TileBorderPaint() => _tileBorderPaint ??= new SKPaint { Color = TileBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };

    private Theme(string name, SKColor bg, SKColor tileBg, SKColor tileBorder, SKColor textPrimary,
        SKColor textSecondary, SKColor accent, SKColor accentSoft, SKColor sparkline, SKColor footerBand, SKColor paneBg)
    {
        Name = name;
        Background = bg;
        TileBackground = tileBg;
        TileBorder = tileBorder;
        TextPrimary = textPrimary;
        TextSecondary = textSecondary;
        Accent = accent;
        AccentSoft = accentSoft;
        Sparkline = sparkline;
        FooterBand = footerBand;
        PaneBackground = paneBg;
    }

    // --- Presets ---

    /// <summary>Original dark palette (default).</summary>
    public static readonly Theme Midnight = new(
        "Midnight",
        new SKColor(0x0E, 0x11, 0x16), // bg
        new SKColor(0x17, 0x1C, 0x24), // tile
        new SKColor(0x26, 0x2D, 0x38), // border
        new SKColor(0xF2, 0xF5, 0xF8), // text primary
        new SKColor(0x8B, 0x95, 0xA3), // text secondary
        new SKColor(0x4C, 0x9A, 0xFF), // accent
        new SKColor(0x1E, 0x3A, 0x66), // accent soft
        new SKColor(0x5C, 0xB8, 0x5C), // sparkline
        new SKColor(0x14, 0x18, 0x20), // footer band
        new SKColor(0x1C, 0x22, 0x2C)); // pane bg

    /// <summary>OLED true-black dark variant — pure black background for panels.</summary>
    public static readonly Theme Obsidian = new(
        "Obsidian",
        new SKColor(0x00, 0x00, 0x00), // true black bg
        new SKColor(0x12, 0x14, 0x18), // tile
        new SKColor(0x2A, 0x2E, 0x36), // border
        new SKColor(0xF4, 0xF6, 0xF8), // text primary
        new SKColor(0x9A, 0xA2, 0xB0), // text secondary
        new SKColor(0x5B, 0xA8, 0xFF), // accent (slightly brighter blue for OLED)
        new SKColor(0x16, 0x2C, 0x4A), // accent soft
        new SKColor(0x57, 0xC8, 0x7A), // sparkline
        new SKColor(0x0A, 0x0C, 0x0F), // footer band
        new SKColor(0x16, 0x19, 0x1F)); // pane bg

    /// <summary>Legible light theme for bright rooms / daytime use.</summary>
    public static readonly Theme Daybreak = new(
        "Daybreak",
        new SKColor(0xEC, 0xEF, 0xF3), // bg
        new SKColor(0xF7, 0xF9, 0xFC), // tile
        new SKColor(0xD3, 0xD9, 0xE2), // border
        new SKColor(0x1B, 0x21, 0x2A), // text primary
        new SKColor(0x5A, 0x63, 0x70), // text secondary
        new SKColor(0x2F, 0x6F, 0xE0), // accent
        new SKColor(0xD7, 0xE2, 0xFB), // accent soft
        new SKColor(0x2E, 0x9E, 0x4F), // sparkline
        new SKColor(0xE1, 0xE5, 0xEB), // footer band
        new SKColor(0xFF, 0xFF, 0xFF)); // pane bg

    public static IReadOnlyList<Theme> All { get; } = new[] { Midnight, Obsidian, Daybreak };

    /// <summary>Resolves a theme by name, falling back to Midnight.</summary>
    public static Theme FromName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return Midnight;
        foreach (var t in All)
            if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                return t;
        return Midnight;
    }
}
