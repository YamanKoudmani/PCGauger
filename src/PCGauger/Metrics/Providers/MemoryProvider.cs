using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCGauger.Metrics;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// RAM metrics via GlobalMemoryStatusEx (unprivileged). Reports total/used/free
/// physical RAM and committed/pagefile usage. Refresh target is 500ms.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MemoryProvider : IMetricProvider
{
    private ulong _totalPhys;
    private ulong _availPhys;
    private ulong _totalPageFile;
    private ulong _availPageFile;
    private uint _memoryLoad;

    public void Update(TimeSpan elapsed)
    {
        var status = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
        };
        if (NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            _totalPhys = status.ullTotalPhys;
            _availPhys = status.ullAvailPhys;
            _totalPageFile = status.ullTotalPageFile;
            _availPageFile = status.ullAvailPageFile;
            _memoryLoad = status.dwMemoryLoad;
        }
    }

    public IEnumerable<Metric> GetMetrics()
    {
        ulong used = _totalPhys - _availPhys;
        yield return Metric.Gauge("mem.load", "RAM", _memoryLoad, "%");
        yield return Metric.Text("mem.used", "Used", used, "B");
        yield return Metric.Text("mem.total", "Total", _totalPhys, "B");
        yield return Metric.Text("mem.free", "Free", _availPhys, "B");
        ulong committed = _totalPageFile - _availPageFile;
        yield return Metric.Text("mem.committed", "Committed", committed, "B");
    }

    public ulong TotalPhys => _totalPhys;
    public ulong AvailPhys => _availPhys;
    public ulong UsedPhys => _totalPhys - _availPhys;
    public uint MemoryLoad => _memoryLoad;
}
