namespace PCGauger.Rendering;

/// <summary>
/// Human-readable formatting for metric values (bytes -> GB, MHz, percentages).
/// </summary>
public static class Format
{
    public static string Bytes(ulong bytes)
    {
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

    public static string Percent(double pct) => $"{pct:0.#}%";
}
