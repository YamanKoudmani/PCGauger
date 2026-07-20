using System.Text.Json;
using PCGauger.Rendering;
using SkiaSharp;

namespace PCGauger.Infrastructure;

/// <summary>
/// Per-tile byte-display mode. Auto = sensible per-kind default (Network shows
/// bits/throughput, other tiles show bytes); Bits forces a bits axis (Mbps/Gbps);
/// Bytes forces a bytes axis (MB/s/GB/s). Magnitude auto-scales in every mode.
/// </summary>
public enum TileUnitMode
{
    Auto,
    Bits,
    Bytes,
}

/// <summary>
/// Per-tile persisted settings: accent override (ARGB, null = use kind default)
/// plus the five Show* flags and the unit mode. Serialized per TileKind.
/// </summary>
public sealed class TileConfig
{
    public uint? AccentArgb { get; set; }
    public bool ShowTitle { get; set; } = true;
    public bool ShowBigValue { get; set; } = true;
    public bool ShowUsageBar { get; set; } = true;
    public bool ShowSparkline { get; set; } = true;
    public bool ShowSecondaryLine { get; set; } = true;
    public TileUnitMode UnitMode { get; set; } = TileUnitMode.Auto;
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
        s.UnitMode = UnitMode;
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
        UnitMode = s.UnitMode,
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
    public bool LaunchAtStartup { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool KioskMode { get; set; }

    /// <summary>Set once a Start Menu shortcut has been created, so a user who
    /// later deletes it manually doesn't get it re-created on every launch.</summary>
    public bool StartMenuRegistered { get; set; }
    public bool ThresholdEnabled { get; set; }
    public double ThresholdPercent { get; set; } = 90;

    /// <summary>Decimal places for displayed metric values (0 = integers only). Default 1.</summary>
    public int ValueDecimals { get; set; } = 1;

    // Global sparkline time span in seconds (shared by every tile). Default 5m.
    public int GraphWindowSeconds { get; set; } = 300;

    // Per-tile settings keyed by ConfigKey ("Kind" or "Kind:DeviceId").
    public Dictionary<string, TileConfig> Tiles { get; set; } = new();

    /// <summary>ConfigKeys of every created instance (enabled or not), in
    /// creation order. Non-empty marks a v2 (migrated) config; an empty list on
    /// a file that has Tiles entries is treated as a v1 file needing migration.</summary>
    public List<string> TileInstances { get; set; } = new();

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

    /// <summary>Per-instance settings lookup by ConfigKey. Creates a default
    /// entry when missing so callers can always read/write safely.</summary>
    public TileConfig Tile(string configKey)
    {
        if (!Tiles.TryGetValue(configKey, out var tc))
        {
            tc = new TileConfig();
            Tiles[configKey] = tc;
        }
        return tc;
    }

    public void SetDetachedPosition(TileKind kind, System.Drawing.Point p)
        => DetachedPositions[kind.ToString()] = new[] { p.X, p.Y };

    public void SetDetachedPosition(string configKey, System.Drawing.Point p)
        => DetachedPositions[configKey] = new[] { p.X, p.Y };

    public bool TryGetDetachedPosition(TileKind kind, out System.Drawing.Point p)
        => TryGetDetachedPosition(kind.ToString(), out p);

    public bool TryGetDetachedPosition(string configKey, out System.Drawing.Point p)
    {
        if (DetachedPositions.TryGetValue(configKey, out var a) && a.Length == 2)
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
