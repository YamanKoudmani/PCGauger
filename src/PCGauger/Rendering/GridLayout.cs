using SkiaSharp;
using PCGauger.Metrics;

namespace PCGauger.Rendering;

/// <summary>
/// Identifies a metric tile. The grid lays these out; detaching moves one into
/// its own window. Adding a new TileKind later (Network) requires no layout
/// changes — the grid reflows automatically.
/// </summary>
public enum TileKind
{
    Cpu,
    Ram,
    Gpu,
    Disk,
    Network,
}

/// <summary>
/// A single dashboard tile: what it shows, and how to draw it into a rectangle.
/// Drawing is supplied by the host (MainForm) so the tile stays a layout/data
/// unit while rendering stays in TileRenderer.
/// </summary>
public sealed class Tile
{
    public TileKind Kind { get; }
    public string Title { get; }
    public Action<SKCanvas, SKRect> Draw { get; }

    /// <summary>
    /// Device identity for multi-instance kinds (Disk/Gpu/Network): the bound
    /// <see cref="DeviceDescriptor.Id"/>. Empty for singleton kinds (Cpu/Ram).
    /// </summary>
    public string InstanceId { get; private set; }

    /// <summary>Persistence key: Kind name for singletons, "Kind:DeviceId" for instances.</summary>
    public string ConfigKey => InstanceId.Length == 0 ? Kind.ToString() : $"{Kind}:{InstanceId}";

    /// <summary>True when this tile can be pointed at a different device (multi-instance kinds).</summary>
    public bool IsDeviceSelectable => DeviceSource != null;

    /// <summary>Device list feeding the settings-pane selector. Null for singleton kinds. Set by MainForm.</summary>
    public Func<IReadOnlyList<DeviceDescriptor>>? DeviceSource { get; set; }

    /// <summary>Called by the settings pane when the user picks a different device (arg = DeviceDescriptor.Id).</summary>
    public Action<string>? DevicePicked { get; set; }

    /// <summary>Bound-device label shown as the header subtitle and in the settings pane. Null when unknown.</summary>
    public string? DeviceDisplayName { get; set; }

    /// <summary>False when the bound device is currently missing (unplugged/disabled) — the tile renders its unavailable state.</summary>
    public bool DeviceAvailable { get; set; } = true;

    /// <summary>
    /// Per-tile appearance/content state. Travels with the tile when it is
    /// detached, since it lives here.
    /// </summary>
    public TileSettings Settings { get; } = new();

    public Tile(TileKind kind, string title, Action<SKCanvas, SKRect> draw, string instanceId = "")
    {
        Kind = kind;
        Title = title;
        Draw = draw;
        InstanceId = instanceId;
    }

    /// <summary>Re-points this tile at a different device, preserving settings. Called by MainForm on rebind.</summary>
    public void UpdateDeviceBinding(string instanceId, string? displayName, bool available)
    {
        InstanceId = instanceId;
        DeviceDisplayName = displayName;
        DeviceAvailable = available;
    }
}

/// <summary>
/// Computes a responsive grid of tile rectangles for a given area and tile
/// count. Column count is chosen from the area's aspect ratio so tiles stay
/// reasonably proportioned on both a tall mini-display and a wide one. This is
/// the "adaptive grid" from the plan (item 12) — replaces hand-placed tiles.
/// </summary>
public static class GridLayout
{
    public static IReadOnlyList<SKRect> Compute(SKRect area, int count, float gap)
    {
        if (count <= 0) return Array.Empty<SKRect>();
        int cols = ChooseColumns(area.Width, area.Height, count);
        int rows = (int)Math.Ceiling((double)count / cols);

        float totalW = area.Width - gap * (cols - 1);
        float totalH = area.Height - gap * (rows - 1);
        float cellW = totalW / cols;
        float cellH = totalH / rows;

        var rects = new List<SKRect>(count);
        for (int i = 0; i < count; i++)
        {
            int c = i % cols;
            int r = i / cols;
            float x = area.Left + c * (cellW + gap);
            float y = area.Top + r * (cellH + gap);
            rects.Add(new SKRect(x, y, x + cellW, y + cellH));
        }
        return rects;
    }

    private static int ChooseColumns(float w, float h, int count)
    {
        if (count <= 1) return 1;
        // Prefer a layout close to square cells. Estimate columns that keep
        // cell aspect ratio near 1.4:1 (wider than tall, good for metric tiles).
        float aspect = w / Math.Max(1f, h);
        int cols = (int)Math.Max(1, Math.Round(Math.Sqrt(count * aspect)));
        cols = Math.Clamp(cols, 1, count);
        return cols;
    }
}
