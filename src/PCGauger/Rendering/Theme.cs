using SkiaSharp;

namespace PCGauger.Rendering;

/// <summary>
/// One solid default theme for v1 (themes beyond this are a later chunk).
/// Centralized so the renderer stays declarative and a theme swap later is a
/// one-line change.
/// </summary>
public sealed class Theme
{
    public SKColor Background { get; } = new SKColor(0x0E, 0x11, 0x16);
    public SKColor TileBackground { get; } = new SKColor(0x17, 0x1C, 0x24);
    public SKColor TileBorder { get; } = new SKColor(0x26, 0x2D, 0x38);
    public SKColor TextPrimary { get; } = new SKColor(0xF2, 0xF5, 0xF8);
    public SKColor TextSecondary { get; } = new SKColor(0x8B, 0x95, 0xA3);
    public SKColor Accent { get; } = new SKColor(0x4C, 0x9A, 0xFF);
    public SKColor AccentSoft { get; } = new SKColor(0x1E, 0x3A, 0x66);
    public SKColor Sparkline { get; } = new SKColor(0x5C, 0xB8, 0x5C);

    public SKPaint BackgroundPaint() => new SKPaint { Color = Background, Style = SKPaintStyle.Fill };
    public SKPaint TilePaint() => new SKPaint { Color = TileBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
    public SKPaint TileBorderPaint() => new SKPaint { Color = TileBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
}
