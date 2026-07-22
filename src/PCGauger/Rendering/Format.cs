using PCGauger.Infrastructure;

namespace PCGauger.Rendering;

/// <summary>
/// Human-readable formatting for metric values (bytes -> MB/GB, MHz, percentages).
/// Byte formatting is per-tile: each tile resolves its own bits/bytes axis and
/// auto-scales magnitude (no fixed-unit toggle).
/// </summary>
public static class Format
{
    /// <summary>
    /// Resolves whether a tile should display a bits axis (Mbps/Gbps) or a bytes
    /// axis (MB/s/GB/s). Auto picks bits for Network throughput, bytes otherwise.
    /// </summary>
    public static bool ResolveBits(TileUnitMode mode, TileKind kind)
        => mode == TileUnitMode.Bits
        || (mode == TileUnitMode.Auto && kind == TileKind.Network);

    /// <summary>
    /// Formats a byte count as a storage size (e.g. "8.0 GB", "512 MB"). The axis
    /// (bits/bytes) and magnitude auto-scale; <paramref name="kind"/> selects the
    /// Auto default.
    /// </summary>
    public static string Size(ulong bytes, TileUnitMode mode, TileKind kind)
        => FormatBytes(bytes, ResolveBits(mode, kind), false);

    /// <summary>
    /// Formats a byte rate as a throughput (e.g. "120 Mbps", "1.2 GB/s"). The axis
    /// (bits/bytes) and magnitude auto-scale; <paramref name="kind"/> selects the
    /// Auto default.
    /// </summary>
    public static string Rate(ulong bytesPerSec, TileUnitMode mode, TileKind kind)
        => FormatBytes(bytesPerSec, ResolveBits(mode, kind), true);

    private static string FormatBytes(ulong value, bool bits, bool rate)
    {
        // Bits axis: convert bytes -> bits.
        double v = bits ? value * 8.0 : value;
        string[] units = bits
            ? rate
                ? new[] { "bps", "Kbps", "Mbps", "Gbps", "Tbps" }
                : new[] { "b", "Kb", "Mb", "Gb", "Tb" }
            : new[] { "B", "KB", "MB", "GB", "TB" };
        // Scaling: binary (1024) for byte quantities/rates and bit quantities —
        // the Windows convention for RAM, VRAM and disk (matches Task Manager,
        // Explorer, and this app's own 1024-based axis ladder). Decimal (1000)
        // only for bit RATES, where networking convention is decimal (a 1 Gbps
        // link is 10^9 bits/s).
        double step = bits && rate ? 1000.0 : 1024.0;
        int i = 0;
        while (v >= step && i < units.Length - 1)
        {
            v /= step;
            i++;
        }
        // Bits units already carry "ps" (per second); only the bytes axis needs
        // an explicit "/s" for rates.
        string suffix = rate && !bits ? $"/s" : "";
        // Small values (b/Kb, B/KB) get two decimals; larger units one.
        string num = i <= 1 ? $"{v:0.##}" : $"{v:0.#}";
        return $"{num} {units[i]}{suffix}";
    }

    public static string Mhz(uint mhz) => $"{mhz:N0} MHz";

    /// <summary>
    /// Live CPU clock. Shows GHz with two decimals when the value is >= 1000 MHz
    /// (e.g. "4.21 GHz"), otherwise plain MHz (e.g. "933 MHz"). The value is a
    /// live current speed, so no "max" label is needed.
    /// </summary>
    public static string Clock(uint mhz) => mhz >= 1000
        ? $"{mhz / 1000.0:0.00} GHz"
        : $"{mhz:N0} MHz";

    /// <summary>
    /// Decimal places for displayed metric values (0 = integers only), set from
    /// AppConfig on load/change. Default 1 = current "0.#" behavior.
    /// </summary>
    public static int ValueDecimals { get; set; } = 1;

    /// <summary>A metric value at the configured precision (no unit suffix).</summary>
    public static string Value(double v) => ValueDecimals switch
    {
        <= 0 => $"{v:0}",
        1 => $"{v:0.#}",
        _ => $"{v:0.##}",
    };

    public static string Percent(double pct) => Value(pct) + "%";
}
