using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCGauger.Metrics;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// Stretch feature (chunk 1, item 7): top process by CPU, by RAM, and by GPU.
/// CPU/RAM use unprivileged Process enumeration — WorkingSet64 for RAM and a
/// TotalProcessorTime delta for CPU%. GPU uses the same PDH wildcard counter
/// family GpuProvider consumes ("\GPU Engine(*)\Utilization Percentage"); each
/// instance name encodes the owning pid as "pid_&lt;n&gt;_luid_..._engtype_...".
/// Cheap because we already poll on a timer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TopProcessProvider : IMetricProvider
{
    private readonly Dictionary<int, (string Name, TimeSpan Cpu)> _prev = new();
    private string _topCpuName = "-";
    private double _topCpuPct;
    private string _topRamName = "-";
    private ulong _topRamBytes;

    // GPU: PDH query over the wildcard GPU Engine counter.
    private IntPtr _gpuQuery;
    private IntPtr _gpuCounter;
    private string _topGpuName = "-";
    private double _topGpuPct;

    // Cache pid -> friendly name. Lookups are expensive (Process construction),
    // so we keep the last good name even across polls. Dead pids are pruned
    // lazily when name resolution fails.
    private readonly Dictionary<int, string> _pidNames = new();

    public TopProcessProvider()
    {
        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _gpuQuery) == 0)
        {
            if (NativeMethods.PdhAddCounter(_gpuQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
                _gpuCounter = IntPtr.Zero;
        }
    }

    public void Update(TimeSpan elapsed)
    {
        double elapsedSec = elapsed.TotalSeconds;
        if (elapsedSec <= 0) elapsedSec = 0.25;

        UpdateCpuRam(elapsedSec);
        UpdateGpu();
    }

    private void UpdateCpuRam(double elapsedSec)
    {
        Process[] processes = Process.GetProcesses();
        string topCpu = "-";
        double topCpuPct = 0;
        string topRam = "-";
        ulong topRamBytes = 0;

        foreach (var p in processes)
        {
            try
            {
                ulong ws = (ulong)p.WorkingSet64;
                if (ws > topRamBytes)
                {
                    topRamBytes = ws;
                    topRam = p.ProcessName;
                }

                var cpu = p.TotalProcessorTime;
                if (_prev.TryGetValue(p.Id, out var prev))
                {
                    double deltaSec = (cpu - prev.Cpu).TotalSeconds;
                    double pct = (deltaSec / elapsedSec) * 100.0 / Environment.ProcessorCount;
                    if (pct > topCpuPct)
                    {
                        topCpuPct = pct;
                        topCpu = p.ProcessName;
                    }
                }
                _prev[p.Id] = (p.ProcessName, cpu);
            }
            catch
            {
                // Access denied / exited mid-enumeration — skip.
            }
        }

        _topCpuName = topCpu;
        _topCpuPct = topCpuPct;
        _topRamName = topRam;
        _topRamBytes = topRamBytes;
    }

    private void UpdateGpu()
    {
        string topGpu = "-";
        double topGpuPct = 0;

        try
        {
            if (_gpuCounter != IntPtr.Zero && NativeMethods.PdhCollectQueryData(_gpuQuery) == 0)
            {
                uint size = 0;
                uint count = 0;
                // First call: get required buffer size.
                int hr = NativeMethods.PdhGetFormattedCounterArrayW(_gpuCounter, NativeMethods.PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero);
                if (size > 0)
                {
                    IntPtr buffer = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        hr = NativeMethods.PdhGetFormattedCounterArrayW(_gpuCounter, NativeMethods.PDH_FMT_DOUBLE, ref size, ref count, buffer);
                        if (hr == 0 && count > 0)
                        {
                            // Sum utilization percentage per pid across all
                            // engine instances (3D, Compute, Copy, ...).
                            var perPid = new Dictionary<int, double>();
                            int stride = Marshal.SizeOf<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>();
                            for (int i = 0; i < count; i++)
                            {
                                var item = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>(buffer + i * stride);
                                int pid = ParsePid(item.szName);
                                if (pid < 0) continue;
                                perPid.TryGetValue(pid, out double cur);
                                perPid[pid] = cur + item.FmtValue.DoubleValue;
                            }

                            double best = 0;
                            int bestPid = -1;
                            foreach (var kv in perPid)
                            {
                                if (kv.Value > best)
                                {
                                    best = kv.Value;
                                    bestPid = kv.Key;
                                }
                            }

                            if (bestPid >= 0)
                            {
                                topGpuPct = best;
                                topGpu = ResolveName(bestPid);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
        }
        catch
        {
            // PDH failures must never escape Update — keep last good values.
        }

        _topGpuName = topGpu;
        _topGpuPct = topGpuPct;
    }

    /// <summary>
    /// Parse the owning pid from a GPU Engine instance name of the form
    /// "pid_1234_luid_0x..._engtype_3D". Returns -1 if the token is missing
    /// or malformed (defends against vendor quirks / future naming changes).
    /// </summary>
    private static int ParsePid(IntPtr szName)
    {
        if (szName == IntPtr.Zero) return -1;
        string? name = Marshal.PtrToStringUni(szName);
        if (string.IsNullOrEmpty(name)) return -1;

        int idx = name.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return -1;

        int start = idx + 4;
        int end = start;
        while (end < name.Length && char.IsDigit(name[end])) end++;
        if (end == start) return -1;
        if (!int.TryParse(name.Substring(start, end - start), out int pid)) return -1;
        return pid;
    }

    /// <summary>
    /// Resolve a friendly process name from a pid, caching results. The process
    /// may have exited between sampling and resolution — never throw; fall back
    /// to "pid &lt;n&gt;" and drop the dead entry from the cache.
    /// </summary>
    private string ResolveName(int pid)
    {
        if (_pidNames.TryGetValue(pid, out var cached) && cached != "-")
            return cached;

        string name = "-";
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            _pidNames[pid] = name;
        }
        catch
        {
            // Process exited (or access denied) between sampling and now.
            // Fall back to "pid <n>" and prune the cache so we don't keep
            // dead entries around forever.
            name = $"pid {pid}";
            _pidNames.Remove(pid);
        }

        return name;
    }

    public IEnumerable<Metric> GetMetrics()
    {
        yield return Metric.Text("proc.topcpu.name", "Top CPU", 0, _topCpuName);
        yield return Metric.Text("proc.topcpu.pct", "Top CPU %", _topCpuPct, "%");
        yield return Metric.Text("proc.topram.name", "Top RAM", 0, _topRamName);
        yield return Metric.Text("proc.topram.bytes", "Top RAM", _topRamBytes, "B");
        yield return Metric.Text("proc.topgpu.name", "Top GPU", 0, _topGpuName);
        yield return Metric.Text("proc.topgpu.pct", "Top GPU %", _topGpuPct, "%");
    }

    public string TopCpuName => _topCpuName;
    public double TopCpuPct => _topCpuPct;
    public string TopRamName => _topRamName;
    public ulong TopRamBytes => _topRamBytes;
    public string TopGpuName => _topGpuName;
    public double TopGpuPct => _topGpuPct;

    public void Dispose()
    {
        if (_gpuCounter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_gpuCounter);
        if (_gpuQuery != IntPtr.Zero) NativeMethods.PdhCloseQuery(_gpuQuery);
    }
}
