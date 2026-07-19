using PCGauger.Infrastructure;

namespace PCGauger.Rendering;

/// <summary>
/// Human-readable formatting for metric values (bytes -> MB/GB, MHz, percentages).
/// </summary>
public static class Format
{
    /// <summary>
    /// Current byte-display mode, set from AppConfig on load/change. Auto scales
    /// adaptively; MB/GB force a fixed unit.
    /// </summary>
    public static UnitsMode Units { get; set; } = UnitsMode.Auto;

    public static string Bytes(ulong bytes)
    {
        if (Units == UnitsMode.MB)
            return $"{bytes / 1024.0 / 1024.0:0.0} MB";
        if (Units == UnitsMode.GB)
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB";

        // Auto: adaptive scaling (original behavior).
        double v = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return $"{v:0.##} {units[i]}";
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
