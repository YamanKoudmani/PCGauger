using SkiaSharp;

namespace PCGauger.Rendering;

/// <summary>
/// Per-tile appearance + content state. Lives on the Tile so it travels with
/// the tile when it is detached into its own window. Session-scoped only —
/// nothing here is persisted.
/// </summary>
public sealed class TileSettings
{
    /// <summary>
    /// Per-tile accent. Null means "use the default accent for this kind", which
    /// <see cref="TilePalette.DefaultFor"/> resolves. Set it to override.
    /// </summary>
    public SKColor? AccentColor { get; set; }

    public bool ShowTitle { get; set; } = true;
    public bool ShowBigValue { get; set; } = true;
    public bool ShowUsageBar { get; set; } = true;
    public bool ShowSparkline { get; set; } = true;
    public bool ShowSecondaryLine { get; set; } = true;
}
