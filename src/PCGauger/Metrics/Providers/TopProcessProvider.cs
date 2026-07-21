using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCGauger.Metrics;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// Stretch feature (chunk 1, item 7): top process by CPU, by RAM, by disk I/O,
/// and by GPU.
///
/// CPU / RAM / Disk come from ONE PDH query holding three wildcard counters
/// ("\Process(*)\% Processor Time", "\Process(*)\Working Set",
/// "\Process(*)\IO Data Bytes/sec"): a single PdhCollectQueryData plus three
/// formatted-array reads yields every process's three metrics AND its display
/// name (the instance string). This replaces the old implementation, which
/// per 1s tick allocated ~200-300 Process objects (Process.GetProcesses),
/// made ~3 syscalls per process (WorkingSet64 / TotalProcessorTime /
/// ProcessName), re-enumerated the "Process" category instance names, and
/// called NextValue() on ~200+ cached PerformanceCounter objects — the single
/// most expensive provider tick in the app by an order of magnitude.
///
/// Semantics preserved: names have no ".exe" and duplicate instances' "#n"
/// suffix is stripped for display (as before); CPU% is divided by the core
/// count (PDH's % Processor Time sums across cores; the old delta math did
/// the same normalization); "Idle" and "_Total" instances are filtered
/// (Process.GetProcesses never returned the Idle process). Rate counters
/// ("% Processor Time", "IO Data Bytes/sec") report no data on the first
/// collect — the footer shows its "-" warm-up state for one tick, exactly
/// like the old cached-counter design. GPU top still uses the same PDH
/// wildcard counter family GpuProvider consumes ("\GPU Engine(*)\Utilization
/// Percentage"); each instance name encodes the owning pid as "pid_&lt;n&gt;_...".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TopProcessProvider : IMetricProvider
{
    // One PDH query for all three process-wide tops.
    private IntPtr _procQuery;
    private IntPtr _cpuCounter;   // \Process(*)\% Processor Time
    private IntPtr _wsCounter;    // \Process(*)\Working Set (bytes, instantaneous)
    private IntPtr _ioCounter;    // \Process(*)\IO Data Bytes/sec

    private string _topCpuName = "-";
    private double _topCpuPct;
    private string _topRamName = "-";
    private ulong _topRamBytes;
    private string _topDiskName = "-";
    private double _topDiskBps;

    // GPU: PDH query over the wildcard GPU Engine counter.
    private IntPtr _gpuQuery;
    private IntPtr _gpuCounter;
    private string _topGpuName = "-";
    private double _topGpuPct;

    // Cache pid -> friendly name (GPU top only). Lookups are expensive (Process
    // construction), so we keep the last good name even across polls. Dead pids
    // are pruned lazily when name resolution fails.
    private readonly Dictionary<int, string> _pidNames = new();

    public TopProcessProvider()
    {
        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _gpuQuery) == 0)
        {
            if (NativeMethods.PdhAddCounter(_gpuQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
                _gpuCounter = IntPtr.Zero;
        }

        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _procQuery) == 0)
        {
            if (NativeMethods.PdhAddCounter(_procQuery, @"\Process(*)\% Processor Time", IntPtr.Zero, out _cpuCounter) != 0)
                _cpuCounter = IntPtr.Zero;
            if (NativeMethods.PdhAddCounter(_procQuery, @"\Process(*)\Working Set", IntPtr.Zero, out _wsCounter) != 0)
                _wsCounter = IntPtr.Zero;
            if (NativeMethods.PdhAddCounter(_procQuery, @"\Process(*)\IO Data Bytes/sec", IntPtr.Zero, out _ioCounter) != 0)
                _ioCounter = IntPtr.Zero;
        }
    }

    public void Update(TimeSpan elapsed)
    {
        UpdateProc();
        UpdateGpu();
    }

    private void UpdateProc()
    {
        try
        {
            if (_procQuery == IntPtr.Zero || NativeMethods.PdhCollectQueryData(_procQuery) != 0)
                return; // PDH unavailable or a transient collect failure: keep last good.

            // CPU: PDH sums the process's threads across ALL cores; divide by
            // the core count to match the old delta-based normalization.
            if (_cpuCounter != IntPtr.Zero)
            {
                string top = "-";
                double topPct = 0;
                foreach (var (name, value) in ReadCounterArray(_cpuCounter))
                {
                    double pct = value / Environment.ProcessorCount;
                    if (pct > topPct)
                    {
                        topPct = pct;
                        top = name;
                    }
                }
                // A no-data first sample (rate counters need two collects)
                // yields no winner — keep last good values in that case.
                if (top != "-")
                {
                    _topCpuName = top;
                    _topCpuPct = topPct;
                }
            }

            // RAM: Working Set is instantaneous, so it has data from the very
            // first collect.
            if (_wsCounter != IntPtr.Zero)
            {
                string top = "-";
                double topBytes = 0;
                foreach (var (name, value) in ReadCounterArray(_wsCounter))
                {
                    if (value > topBytes)
                    {
                        topBytes = value;
                        top = name;
                    }
                }
                if (top != "-")
                {
                    _topRamName = top;
                    _topRamBytes = (ulong)Math.Max(0, topBytes);
                }
            }

            // Disk: same counter the old PerformanceCounter cache read, but as
            // one formatted-array read instead of ~200+ per-instance NextValue
            // calls. Rate counter — no winner on the first collect.
            if (_ioCounter != IntPtr.Zero)
            {
                string top = "-";
                double topBps = 0;
                foreach (var (name, value) in ReadCounterArray(_ioCounter))
                {
                    if (value > topBps)
                    {
                        topBps = value;
                        top = name;
                    }
                }
                if (top != "-")
                {
                    _topDiskName = top;
                    _topDiskBps = topBps;
                }
            }
        }
        catch
        {
            // PDH failures must never escape Update — keep last good values.
        }
    }

    /// <summary>
    /// Reads a wildcard counter's formatted array and yields (displayName, value)
    /// per live instance. Instance names are process names without ".exe";
    /// duplicate instances ("chrome#2") are stripped to "chrome" for display.
    /// "_Total" and "Idle" are filtered (the old Process enumeration never
    /// included the Idle process; _Total is an aggregate, not a process).
    /// Items with a non-zero CStatus (e.g. rate counters before their second
    /// sample) are skipped.
    /// </summary>
    private static IEnumerable<(string Name, double Value)> ReadCounterArray(IntPtr counter)
    {
        uint size = 0;
        uint count = 0;
        // First call: get required buffer size.
        NativeMethods.PdhGetFormattedCounterArrayW(counter, NativeMethods.PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero);
        if (size <= 0) yield break;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            int hr = NativeMethods.PdhGetFormattedCounterArrayW(counter, NativeMethods.PDH_FMT_DOUBLE, ref size, ref count, buffer);
            if (hr != 0 || count == 0) yield break;

            int stride = Marshal.SizeOf<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>(buffer + i * stride);
                if (item.FmtValue.CStatus != 0) continue; // no data for this instance yet
                if (item.szName == IntPtr.Zero) continue;
                string? raw = Marshal.PtrToStringUni(item.szName);
                if (string.IsNullOrEmpty(raw)) continue;
                if (raw.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                if (raw.Equals("Idle", StringComparison.OrdinalIgnoreCase)) continue;
                int hash = raw.IndexOf('#');
                yield return (hash < 0 ? raw : raw.Substring(0, hash), item.FmtValue.DoubleValue);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
            using var p = System.Diagnostics.Process.GetProcessById(pid);
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
        yield return Metric.Text("proc.topdisk.name", "Top Disk", 0, _topDiskName);
        yield return Metric.Text("proc.topdisk.bps", "Top Disk", _topDiskBps, "B/s");
    }

    public string TopCpuName => _topCpuName;
    public double TopCpuPct => _topCpuPct;
    public string TopRamName => _topRamName;
    public ulong TopRamBytes => _topRamBytes;
    public string TopGpuName => _topGpuName;
    public double TopGpuPct => _topGpuPct;
    public string TopDiskName => _topDiskName;
    public double TopDiskBps => _topDiskBps;

    public void Dispose()
    {
        if (_gpuCounter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_gpuCounter);
        if (_gpuQuery != IntPtr.Zero) NativeMethods.PdhCloseQuery(_gpuQuery);
        if (_cpuCounter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_cpuCounter);
        if (_wsCounter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_wsCounter);
        if (_ioCounter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_ioCounter);
        if (_procQuery != IntPtr.Zero) NativeMethods.PdhCloseQuery(_procQuery);
    }
}
