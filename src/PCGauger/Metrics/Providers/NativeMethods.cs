using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// P/Invoke declarations for the unmanaged Windows APIs used by the metric
/// providers. All calls here are non-privileged (no admin required in v1).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    // ---- CPU: aggregate idle/time accounting ----
    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(
        out FILETIME lpIdleTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    // ---- CPU: per-core frequency + usage ----
    public enum SYSTEM_INFORMATION_CLASS
    {
        SystemProcessorPerformanceInformation = 8,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public ulong IdleTime;
        public ulong KernelTime;
        public ulong UserTime;
        public ulong DpcTime;
        public ulong InterruptTime;
        public uint InterruptCount;
    }

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(
        SYSTEM_INFORMATION_CLASS infoClass,
        IntPtr info,
        uint infoLength,
        out uint returnLength);

    // ---- CPU: per-core frequency via power info ----
    public enum POWER_INFORMATION_LEVEL
    {
        ProcessorInformation = 11,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [DllImport("powrprof.dll")]
    public static extern int CallNtPowerInformation(
        POWER_INFORMATION_LEVEL informationLevel,
        IntPtr inputBuffer,
        uint inputBufferLength,
        IntPtr outputBuffer,
        uint outputBufferLength);

    // ---- Memory ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ---- Disk: capacity ----
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool GetDiskFreeSpaceEx(
        string? lpDirectory,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    // ---- PDH (Performance Data Helper) for GPU Engine + Disk counters ----
    public const int PDH_FMT_DOUBLE = 0x00000200;
    public const int PDH_INVALID_DATA = -2147481648; // 0x800007D0

    [DllImport("pdh.dll", CharSet = CharSet.Auto)]
    public static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Auto)]
    public static extern int PdhAddCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern int PdhGetFormattedCounterValue(IntPtr counter, int format, out IntPtr type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll")]
    public static extern int PdhRemoveCounter(IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern int PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern int PdhGetFormattedCounterArrayW(
        IntPtr hCounter,
        int dwFormat,
        ref uint dwBufferSize,
        ref uint itemCount,
        IntPtr itemBuffer);

    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr szName;            // LPWSTR
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE
    {
        public int CStatus;
        public double DoubleValue;
        public long LongValue;
        public long LargeValue;
        public int AnsiStringLength;
        public int WideStringLength;
    }

    // ---- DXGI for VRAM (IDXGIAdapter3::QueryVideoMemoryInfo) ----
    public enum DXGI_MEMORY_SEGMENT_GROUP : uint
    {
        Local = 0,
        NonLocal = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    public static ulong FileTimeToUlong(FILETIME ft)
        => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
}
