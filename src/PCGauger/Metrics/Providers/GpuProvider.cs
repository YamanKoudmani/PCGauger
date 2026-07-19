using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCGauger.Metrics;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// GPU metrics via unprivileged APIs:
/// - Aggregate GPU utilization from PDH "\GPU Engine(*)\Utilization Percentage".
///   The wildcard counter is added once; each update reads the formatted array
///   (PdhGetFormattedCounterArrayW) and sums every instance. This is the
///   canonical way to consume a wildcard PDH counter and avoids the fragile
///   PdhExpandWildCardPath enumeration.
/// - VRAM usage from PDH "\GPU Adapter Memory(*)\Dedicated Usage" (bytes),
///   falling back to "\GPU Local Adapter Memory(*)\Local Usage", then to
///   DXGI IDXGIAdapter3::QueryVideoMemoryInfo (Local). Budget stays on DXGI.
///
/// No admin required. VRAM read is best-effort and never breaks the tile.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GpuProvider : IMetricProvider
{
    private IntPtr _query;
    private IntPtr _counter;
    private double _utilization;
    private ulong _vramUsed;
    private ulong _vramBudget;

    // VRAM-used fallback chain (evaluated each poll; counters are cached):
    //   1. \GPU Adapter Memory(*)\Dedicated Usage  (Task Manager's "Dedicated GPU memory")
    //   2. \GPU Local Adapter Memory(*)\Local Usage
    //   3. DXGI IDXGIAdapter3::QueryVideoMemoryInfo(Local).CurrentUsage
    private IntPtr _vramQuery;
    private IntPtr _vramCounterDedicated;
    private IntPtr _vramCounterLocal;

    public GpuProvider()
    {
        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _query) == 0)
        {
            if (NativeMethods.PdhAddCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
                _counter = IntPtr.Zero;
        }

        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _vramQuery) == 0)
        {
            if (NativeMethods.PdhAddCounter(_vramQuery, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _vramCounterDedicated) != 0)
                _vramCounterDedicated = IntPtr.Zero;
            if (NativeMethods.PdhAddCounter(_vramQuery, @"\GPU Local Adapter Memory(*)\Local Usage", IntPtr.Zero, out _vramCounterLocal) != 0)
                _vramCounterLocal = IntPtr.Zero;
        }
    }

    public void Update(TimeSpan elapsed)
    {
        if (_counter != IntPtr.Zero && NativeMethods.PdhCollectQueryData(_query) == 0)
        {
            uint size = 0;
            uint count = 0;
            // First call: get required buffer size.
            int hr = NativeMethods.PdhGetFormattedCounterArrayW(_counter, NativeMethods.PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero);
            if (size > 0)
            {
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    hr = NativeMethods.PdhGetFormattedCounterArrayW(_counter, NativeMethods.PDH_FMT_DOUBLE, ref size, ref count, buffer);
                    if (hr == 0 && count > 0)
                    {
                        double sum = 0;
                        int stride = Marshal.SizeOf<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>();
                        for (int i = 0; i < count; i++)
                        {
                            var item = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>(buffer + i * stride);
                            sum += item.FmtValue.DoubleValue;
                        }
                        // Clamp: summed engine percentages can exceed 100 on
                        // multi-engine GPUs.
                        _utilization = Math.Min(100.0, sum);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        QueryVram();
    }

    private void QueryVram()
    {
        ulong used = 0;

        // Collect the VRAM query once (covers both counters). The first collect
        // after adding counters returns PDH_NO_DATA — that is expected, not an
        // error; the read below simply yields 0 that poll and real data after.
        if (_vramQuery != IntPtr.Zero)
            NativeMethods.PdhCollectQueryData(_vramQuery);

        // Source 1: \GPU Adapter Memory(*)\Dedicated Usage (bytes).
        if (_vramCounterDedicated != IntPtr.Zero)
            used = SumCounterBytes(_vramCounterDedicated);

        // Source 2: \GPU Local Adapter Memory(*)\Local Usage (bytes).
        if (used == 0 && _vramCounterLocal != IntPtr.Zero)
            used = SumCounterBytes(_vramCounterLocal);

        // Source 3: DXGI CurrentUsage (under-reports / 0 on many drivers).
        if (used == 0)
        {
            try
            {
                var factory = DxgiFactory.Create();
                var adapter = factory.EnumAdapter(0);
                if (adapter != null)
                {
                    var info = adapter.QueryVideoMemoryInfo(NativeMethods.DXGI_MEMORY_SEGMENT_GROUP.Local);
                    used = info.CurrentUsage;
                    _vramBudget = info.Budget;
                    adapter.Dispose();
                }
                factory.Dispose();
            }
            catch
            {
                // Leave last good values.
            }
        }

        if (used > 0) _vramUsed = used;

        // Budget: keep DXGI (reads fine). Re-query only if we didn't already
        // get it from the DXGI fallback above.
        if (_vramBudget == 0)
        {
            try
            {
                var factory = DxgiFactory.Create();
                var adapter = factory.EnumAdapter(0);
                if (adapter != null)
                {
                    var info = adapter.QueryVideoMemoryInfo(NativeMethods.DXGI_MEMORY_SEGMENT_GROUP.Local);
                    _vramBudget = info.Budget;
                    adapter.Dispose();
                }
                factory.Dispose();
            }
            catch
            {
                // Leave last good values.
            }
        }
    }

    /// <summary>
    /// Sum a wildcard PDH counter's instances into a byte total. The owning
    /// query must already have been collected by the caller. Returns 0 on any
    /// failure or when the counter has no data yet. Never throws.
    /// </summary>
    private static ulong SumCounterBytes(IntPtr counter)
    {
        try
        {
            uint size = 0;
            uint count = 0;
            int hr = NativeMethods.PdhGetFormattedCounterArrayW(counter, NativeMethods.PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero);
            if (size <= 0) return 0;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                hr = NativeMethods.PdhGetFormattedCounterArrayW(counter, NativeMethods.PDH_FMT_DOUBLE, ref size, ref count, buffer);
                if (hr != 0 || count == 0) return 0;

                double sum = 0;
                int stride = Marshal.SizeOf<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>(buffer + i * stride);
                    sum += item.FmtValue.DoubleValue;
                }
                return (ulong)Math.Round(sum);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return 0;
        }
    }

    public IEnumerable<Metric> GetMetrics()
    {
        yield return Metric.Gauge("gpu.util", "GPU", _utilization, "%");
        yield return Metric.Text("gpu.vram.used", "VRAM Used", _vramUsed, "B");
        yield return Metric.Text("gpu.vram.budget", "VRAM Total", _vramBudget, "B");
    }

    public double Utilization => _utilization;
    public ulong VramUsed => _vramUsed;
    public ulong VramBudget => _vramBudget;

    public void Dispose()
    {
        if (_counter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_counter);
        if (_query != IntPtr.Zero) NativeMethods.PdhCloseQuery(_query);
        if (_vramCounterDedicated != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_vramCounterDedicated);
        if (_vramCounterLocal != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_vramCounterLocal);
        if (_vramQuery != IntPtr.Zero) NativeMethods.PdhCloseQuery(_vramQuery);
    }
}
