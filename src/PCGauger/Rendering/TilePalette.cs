using SkiaSharp;

namespace PCGauger.Rendering;

/// <summary>
/// Per-kind accent palette and helpers for deriving soft/track variants from a
/// given accent color. The theme's shared Accent/AccentSoft/Sparkline remain as
/// fallbacks but are no longer the per-tile source of truth.
/// </summary>
public static class TilePalette
{
    // Per-kind defaults chosen to sit cleanly on the #171C24 card.
    public static readonly SKColor CpuDefault = new(0x4C, 0x9A, 0xFF); // blue (unchanged)
    public static readonly SKColor RamDefault = new(0x5C, 0xB8, 0x5C); // green (was sparkline)
    public static readonly SKColor GpuDefault = new(0xFF, 0x9F, 0x43); // warm orange
    public static readonly SKColor DiskDefault = new(0xF0, 0x62, 0x92); // pink

    // Second-series colors for dual graphs (chosen to stay distinct from the tile accents).
    public static readonly SKColor DiskRead = new(0x56, 0xC8, 0xFF); // blue — disk read line
    public static readonly SKColor NetUp = new(0xA7, 0x8B, 0xFA); // violet — net upload line
    public static readonly SKColor NetworkDefault = new(0x2D, 0xD4, 0xBF); // teal

    /// <summary>
    /// The default accent for a tile kind. Used when TileSettings.AccentColor
    /// is null.
    /// </summary>
    public static SKColor DefaultFor(TileKind kind) => kind switch
    {
        TileKind.Cpu => CpuDefault,
        TileKind.Ram => RamDefault,
        TileKind.Gpu => GpuDefault,
        TileKind.Disk => DiskDefault,
        TileKind.Network => NetworkDefault,
        _ => CpuDefault,
    };

    /// <summary>
    /// Resolves the effective accent for a tile, falling back to the per-kind
    /// default when the setting is unset.
    /// </summary>
    public static SKColor Resolve(TileKind kind, TileSettings settings)
        => settings.AccentColor ?? DefaultFor(kind);

    /// <summary>
    /// A translucent track/soft variant of the accent, laid over the card
    /// background. Replaces the shared AccentSoft for per-tile usage.
    /// </summary>
    public static SKColor Soft(SKColor accent) => WithAlpha(accent, 0x33);

    /// <summary>
    /// The sparkline area fill: the accent at ~40 alpha over the card.
    /// </summary>
    public static SKColor SparkFill(SKColor accent) => WithAlpha(accent, 0x28);

    private static SKColor WithAlpha(SKColor c, byte a)
        => new(c.Red, c.Green, c.Blue, a);
}
