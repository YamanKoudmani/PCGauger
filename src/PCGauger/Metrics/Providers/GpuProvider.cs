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
/// - VRAM usage from IDXGIAdapter3::QueryVideoMemoryInfo (Local segment).
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

    public GpuProvider()
    {
        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _query) == 0)
        {
            if (NativeMethods.PdhAddCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
                _counter = IntPtr.Zero;
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
        try
        {
            var factory = DxgiFactory.Create();
            var adapter = factory.EnumAdapter(0);
            if (adapter != null)
            {
                var info = adapter.QueryVideoMemoryInfo(NativeMethods.DXGI_MEMORY_SEGMENT_GROUP.Local);
                _vramUsed = info.CurrentUsage;
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
    }
}
