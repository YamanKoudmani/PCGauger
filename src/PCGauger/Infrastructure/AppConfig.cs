using System.Text.Json;
using PCGauger.Rendering;
using SkiaSharp;

namespace PCGauger.Infrastructure;

/// <summary>
/// Display units mode for byte values. Auto = current adaptive scaling.
/// </summary>
public enum UnitsMode
{
    Auto,
    MB,
    GB,
}

/// <summary>
/// Per-tile persisted settings: accent override (ARGB, null = use kind default)
/// plus the five Show* flags. Serialized per TileKind.
/// </summary>
public sealed class TileConfig
{
    public uint? AccentArgb { get; set; }
    public bool ShowTitle { get; set; } = true;
    public bool ShowBigValue { get; set; } = true;
    public bool ShowUsageBar { get; set; } = true;
    public bool ShowSparkline { get; set; } = true;
    public bool ShowSecondaryLine { get; set; } = true;
    // Default true: an existing config.json without this key must deserialize as
    // enabled (a missing entry means "shown", never "hidden").
    public bool Enabled { get; set; } = true;

    public void ApplyTo(TileSettings s)
    {
        s.ShowTitle = ShowTitle;
        s.ShowBigValue = ShowBigValue;
        s.ShowUsageBar = ShowUsageBar;
        s.ShowSparkline = ShowSparkline;
        s.ShowSecondaryLine = ShowSecondaryLine;
        s.AccentColor = AccentArgb.HasValue
            ? new SKColor((byte)(AccentArgb.Value >> 24), (byte)(AccentArgb.Value >> 16), (byte)(AccentArgb.Value >> 8), (byte)AccentArgb.Value)
            : null;
    }

    public static TileConfig From(TileSettings s) => new()
    {
        AccentArgb = s.AccentColor.HasValue
            ? (uint)((s.AccentColor.Value.Alpha << 24) | (s.AccentColor.Value.Red << 16) | (s.AccentColor.Value.Green << 8) | s.AccentColor.Value.Blue)
            : null,
        ShowTitle = s.ShowTitle,
        ShowBigValue = s.ShowBigValue,
        ShowUsageBar = s.ShowUsageBar,
        ShowSparkline = s.ShowSparkline,
        ShowSecondaryLine = s.ShowSecondaryLine,
        Enabled = true,
    };
}

/// <summary>
/// A small JSON config service persisted to
/// %LOCALAPPDATA%\PCGauger\config.json. Tolerates a missing or corrupt file
/// (falls back to defaults, never throws). Saves atomically (temp file +
/// File.Move overwrite). The file is tiny, so it is written immediately on
/// every settings change and on window close.
/// </summary>
public sealed class AppConfig
{
    private static readonly string DirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCGauger");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "config.json");

    public string ThemeName { get; set; } = "Midnight";
    public UnitsMode Units { get; set; } = UnitsMode.Auto;
    public bool LaunchAtStartup { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool KioskMode { get; set; }
    public bool ThresholdEnabled { get; set; }
    public double ThresholdPercent { get; set; } = 90;

    /// <summary>Decimal places for displayed metric values (0 = integers only). Default 1.</summary>
    public int ValueDecimals { get; set; } = 1;

    // Global sparkline time span in seconds (shared by every tile). Default 5m.
    public int GraphWindowSeconds { get; set; } = 300;

    // Per-tile settings keyed by TileKind name.
    public Dictionary<string, TileConfig> Tiles { get; set; } = new();

    // Custom display order of tiles (TileKind names). Empty = use canonical
    // order. Persisted on every committed reorder and applied at startup and on
    // reattach so detaching/reattaching never scrambles a user's layout.
    public List<string> TileOrder { get; set; } = new();

    // Main window bounds (x, y, w, h). 0 = use default/centered.
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowW { get; set; } = 420;
    public int WindowH { get; set; } = 560;

    // Detached window screen positions keyed by TileKind name.
    public Dictionary<string, int[]> DetachedPositions { get; set; } = new();

    public TileConfig Tile(TileKind kind)
    {
        var key = kind.ToString();
        if (!Tiles.TryGetValue(key, out var tc))
        {
            tc = new TileConfig();
            Tiles[key] = tc;
        }
        return tc;
    }

    public void SetDetachedPosition(TileKind kind, System.Drawing.Point p)
        => DetachedPositions[kind.ToString()] = new[] { p.X, p.Y };

    public bool TryGetDetachedPosition(TileKind kind, out System.Drawing.Point p)
    {
        if (DetachedPositions.TryGetValue(kind.ToString(), out var a) && a.Length == 2)
        {
            p = new System.Drawing.Point(a[0], a[1]);
            return true;
        }
        p = System.Drawing.Point.Empty;
        return false;
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // Corrupt or unreadable file: fall back to defaults. Never throw.
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true); // atomic replace
        }
        catch
        {
            // Best-effort persistence; a failed save must not crash the app.
        }
    }
}
