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

    public static ulong FileTimeToUlong(FILETIME ft)
        => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
}
