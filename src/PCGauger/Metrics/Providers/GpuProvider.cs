using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCGauger.Metrics;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// GPU metrics via unprivileged APIs:
/// - GPU utilization from PDH "\GPU Engine(*)\Utilization Percentage", filtered
///   to the instances belonging to one adapter (by LUID). The wildcard counter
///   is added once; each update reads the formatted array
///   (PdhGetFormattedCounterArrayW) and sums the matching instances. This is the
///   canonical way to consume a wildcard PDH counter and avoids the fragile
///   PdhExpandWildCardPath enumeration.
/// - VRAM usage from PDH "\GPU Adapter Memory(*)\Dedicated Usage" (bytes),
///   filtered by the same LUID, falling back to
///   "\GPU Local Adapter Memory(*)\Local Usage", then to
///   DXGI IDXGIAdapter3::QueryVideoMemoryInfo (Local). Budget stays on DXGI.
///
/// No admin required. VRAM read is best-effort and never breaks the tile.
///
/// v2 multi-instance: construct with an adapter index to bind one tile to one
/// GPU. An out-of-range index sets <see cref="DeviceAvailable"/> false and the
/// tile reports zeros; <see cref="Update"/> never throws.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GpuProvider : IMetricProvider
{
    private readonly int _adapterIndex;
    private readonly string? _luidFragment; // "luid_0x{High:X8}_0x{Low:X8}" to match PDH instances

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

    // Cached DXGI handles for the VRAM fallback. Created ONCE (lazily on first
    // need) and reused for both the used and budget queries every poll — the old
    // code rebuilt the factory + adapter + QueryInterface TWICE per 1s poll
    // forever whenever PDH read 0, which is needless churn. Released in Dispose.
    private DxgiFactory.DxgiFactoryHandle? _dxgiFactory;
    private DxgiFactory.DxgiAdapterHandle? _dxgiAdapter;

    // Defense-in-depth: if a provider is ever disposed outside the poller's
    // Remove fence, Update/GetMetrics early-return instead of touching freed
    // native PDH/DXGI handles. Set only in Dispose; read on the hot path.
    private int _disposed;

    /// <summary>
    /// DXGI adapter description (e.g. "NVIDIA GeForce RTX 4070"). Empty for the
    /// parameterless all-adapters ctor or when the adapter can't be described.
    /// </summary>
    public string AdapterName { get; }

    /// <summary>
    /// True while the requested adapter index resolves to a real adapter. False
    /// when the index is out of range (no such GPU) — the tile reports zeros and
    /// never throws.
    /// </summary>
    public bool DeviceAvailable { get; private set; }

    public GpuProvider()
    {
        _adapterIndex = -1;
        _luidFragment = null;
        AdapterName = string.Empty;
        DeviceAvailable = true;
        OpenCounters();
    }

    public GpuProvider(int adapterIndex)
    {
        _adapterIndex = adapterIndex;

        // Resolve the adapter's LUID + description up front. If the index is out
        // of range, mark unavailable and never open counters; Update stays safe.
        string? luidFragment = null;
        string adapterName = string.Empty;
        bool luidZero = false;
        try
        {
            using var factory = DxgiFactory.Create();
            using var adapter = factory.EnumAdapter((uint)adapterIndex);
            if (adapter != null)
            {
                var desc = adapter.GetDesc1();
                if (desc.HasValue)
                {
                    adapterName = desc.Value.Description ?? string.Empty;
                    luidFragment = $"luid_0x{desc.Value.Luid.HighPart:X8}_0x{desc.Value.Luid.LowPart:X8}";
                    // A zero LUID means a virtual/software adapter with no real
                    // identity. The fragment "luid_0x00000000_0x00000000" would
                    // Contains-match EVERY GPU Engine PDH instance and silently
                    // sum all GPUs, so treat it as unavailable and never match.
                    if (desc.Value.Luid.HighPart == 0 && desc.Value.Luid.LowPart == 0)
                        luidZero = true;
                }
            }
        }
        catch
        {
            // Leave unavailable; Update will report zeros.
        }

        AdapterName = adapterName;
        _luidFragment = luidFragment;
        // Available only when we resolved a real (non-zero) LUID. AdapterName is
        // still reported so the tile shows what was found.
        DeviceAvailable = luidFragment != null && !luidZero;

        if (DeviceAvailable)
            OpenCounters();
    }

    private void OpenCounters()
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
        // Disposed guard: never touch native handles after Dispose.
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0) return;

        // Out-of-range adapter: nothing to poll, report zeros. Never throw.
        if (_adapterIndex >= 0 && !DeviceAvailable)
        {
            _utilization = 0;
            _vramUsed = 0;
            _vramBudget = 0;
            return;
        }

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
                            string? name = item.szName != IntPtr.Zero ? Marshal.PtrToStringUni(item.szName) : null;
                            // v2: only count instances for this adapter's LUID.
                            // v1 (parameterless): sum every instance. Exact token
                            // match (not Contains) so a zero-LUID fragment can't
                            // accidentally match every engine instance.
                            if (_luidFragment == null || (name != null && InstanceMatchesLuid(name, _luidFragment)))
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

    /// <summary>
    /// True when a PDH GPU Engine instance name belongs to this adapter's LUID.
    /// PDH names look like "luid_0x00000000_0x00000000_engtype_3D" — we split on
    /// '_' and compare the reconstructed "luid_0x{HIGH:X8}_0x{LOW:X8}" token
    /// against <paramref name="luidFragment"/> with an exact, ordinal comparison.
    /// This avoids the old <c>Contains</c> match that let a zero-LUID fragment
    /// match every engine instance on the box. Allocation-light: only the two
    /// leading tokens are examined on the hot path.
    /// </summary>
    private static bool InstanceMatchesLuid(string instanceName, string luidFragment)
    {
        // Real PDH instance names look like
        // "pid_10528_luid_0x00000000_0x00011DD0_phys_0_eng_0_engtype_3D".
        // The LUID token we want is "luid_0xHHHHHHHH_0xLLLLLLLL" — tokens 3-5
        // (after "pid" and the pid number). Locate "luid_" and take through the
        // second underscore that follows it, then compare ordinally against
        // luidFragment. This avoids the old bug where the first three tokens
        // ("pid_10528_luid") were compared and never matched, leaving every
        // engine instance rejected and the GPU graph stuck at 0%.
        int luidStart = instanceName.IndexOf("luid_", StringComparison.Ordinal);
        if (luidStart < 0) return false;
        int afterLuid = luidStart + "luid_".Length;
        int first = instanceName.IndexOf('_', afterLuid);
        if (first < 0) return false;
        int second = instanceName.IndexOf('_', first + 1);
        int end = second < 0 ? instanceName.Length : second;
        string candidate = instanceName.Substring(luidStart, end - luidStart);
        return candidate.Equals(luidFragment, StringComparison.Ordinal);
    }

    private void QueryVram()
    {
        ulong used = 0;

        // Collect the VRAM query once (covers both counters). The first collect
        // after adding counters returns PDH_NO_DATA — that is expected, not an
        // error; the read below simply yields 0 that poll and real data after.
        if (_vramQuery != IntPtr.Zero)
            NativeMethods.PdhCollectQueryData(_vramQuery);

        // Source 1: \GPU Adapter Memory(*)\Dedicated Usage (bytes), filtered by LUID.
        if (_vramCounterDedicated != IntPtr.Zero)
            used = SumCounterBytes(_vramCounterDedicated);

        // Source 2: \GPU Local Adapter Memory(*)\Local Usage (bytes), filtered by LUID.
        if (used == 0 && _vramCounterLocal != IntPtr.Zero)
            used = SumCounterBytes(_vramCounterLocal);

        // Source 3: DXGI CurrentUsage + Budget. Reuse the cached factory/adapter
        // handles (created once, lazily) instead of rebuilding them every poll.
        if (used == 0 || _vramBudget == 0)
        {
            try
            {
                var adapter = GetDxgiAdapter();
                if (adapter != null)
                {
                    var info = adapter.QueryVideoMemoryInfo(NativeMethods.DXGI_MEMORY_SEGMENT_GROUP.Local);
                    if (used == 0) used = info.CurrentUsage;
                    if (_vramBudget == 0) _vramBudget = info.Budget;
                }
            }
            catch
            {
                // Leave last good values.
            }
        }

        if (used > 0) _vramUsed = used;
    }

    /// <summary>
    /// Returns the cached DXGI adapter handle for <see cref="_adapterIndex"/>,
    /// creating the factory + adapter ONCE on first need. Returns null if DXGI
    /// is unavailable or the adapter can't be opened. The handles are released
    /// in <see cref="Dispose"/>. This removes the per-poll factory/adapter
    /// churn that previously happened whenever PDH VRAM read 0.
    /// </summary>
    private DxgiFactory.DxgiAdapterHandle? GetDxgiAdapter()
    {
        if (_dxgiAdapter != null) return _dxgiAdapter;
        if (_dxgiFactory != null) return null; // creation already failed once

        try
        {
            _dxgiFactory = DxgiFactory.Create();
            _dxgiAdapter = _dxgiFactory.EnumAdapter(_adapterIndex >= 0 ? (uint)_adapterIndex : 0);
            if (_dxgiAdapter == null)
            {
                _dxgiFactory.Dispose();
                _dxgiFactory = null;
            }
        }
        catch
        {
            _dxgiFactory?.Dispose();
            _dxgiFactory = null;
            _dxgiAdapter = null;
        }
        return _dxgiAdapter;
    }

    /// <summary>
    /// Sum a wildcard PDH counter's instances into a byte total, filtered to the
    /// requested adapter's LUID when <see cref="_luidFragment"/> is set. The
    /// owning query must already have been collected by the caller. Returns 0 on
    /// any failure or when the counter has no data yet. Never throws.
    /// </summary>
    private ulong SumCounterBytes(IntPtr counter)
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
                        string? name = item.szName != IntPtr.Zero ? Marshal.PtrToStringUni(item.szName) : null;
                        if (_luidFragment == null || (name != null && InstanceMatchesLuid(name, _luidFragment)))
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
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0) yield break;

        yield return Metric.Gauge("gpu.util", "GPU", _utilization, "%");
        yield return Metric.Text("gpu.vram.used", "VRAM Used", _vramUsed, "B");
        yield return Metric.Text("gpu.vram.budget", "VRAM Total", _vramBudget, "B");
    }

    public double Utilization => _utilization;
    public ulong VramUsed => _vramUsed;
    public ulong VramBudget => _vramBudget;

    public void Dispose()
    {
        // Mark disposed first so a stray Tick (shouldn't happen post-Remove, but
        // defense-in-depth) won't touch freed handles.
        Interlocked.Exchange(ref _disposed, 1);

        if (_counter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_counter);
        if (_query != IntPtr.Zero) NativeMethods.PdhCloseQuery(_query);
        if (_vramCounterDedicated != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_vramCounterDedicated);
        if (_vramCounterLocal != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_vramCounterLocal);
        if (_vramQuery != IntPtr.Zero) NativeMethods.PdhCloseQuery(_vramQuery);

        // Release the cached DXGI handles (created lazily on first VRAM fallback).
        _dxgiAdapter?.Dispose();
        _dxgiAdapter = null;
        _dxgiFactory?.Dispose();
        _dxgiFactory = null;
    }
}
