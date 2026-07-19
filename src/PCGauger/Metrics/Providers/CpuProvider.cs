using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCGauger.Metrics;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// CPU metrics via unprivileged Windows APIs:
/// - Aggregate % from GetSystemTimes (idle vs total across all cores).
/// - Per-core usage from NtQuerySystemInformation(SystemProcessorPerformanceInformation).
/// - Per-core frequency from CallNtPowerInformation(ProcessorInformation).
/// - Core/thread counts from Environment.ProcessorCount.
///
/// Refresh target is 250ms (set by the poller interval). State is accumulated
/// between Update calls so each poll is a delta, not an absolute reading.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CpuProvider : IMetricProvider, IDisposable
{
    private readonly int _coreCount = Environment.ProcessorCount;
    private ulong _prevTotal;
    private ulong _prevIdle;
    private readonly ulong[] _prevCoreTotal;
    private readonly ulong[] _prevCoreIdle;
    private readonly double[] _coreUsage;
    private readonly uint[] _coreMhz;

    private double _aggregateRaw;
    private double _aggregateUsage;
    private uint _maxMhz;
    private uint _currentMhz;

    // PDH counter for dynamic clock: "% Processor Performance" of _Total is the
    // current performance relative to the nominal/max frequency, so
    // CurrentMhz = MaxMhz * pct / 100. This tracks turbo like Task Manager's
    // "Speed" instead of the static value CallNtPowerInformation reports.
    private IntPtr _perfQuery;
    private IntPtr _perfCounter;

    // Real topology (computed once, cached). LogicalProcessors is the logical
    // count; PhysicalCores counts RelationProcessorCore records.
    private readonly int _logicalProcessors = Environment.ProcessorCount;
    private readonly int _physicalCores;

    // Exponential moving average smoothing factor (0..1). Lower = smoother /
    // slower to react. 0.25 keeps the displayed CPU stable instead of flickering
    // between 1s samples, which the user found too jumpy at fast cadences.
    private const double Smoothing = 0.25;

    public CpuProvider()
    {
        _prevCoreTotal = new ulong[_coreCount];
        _prevCoreIdle = new ulong[_coreCount];
        _coreUsage = new double[_coreCount];
        _coreMhz = new uint[_coreCount];
        _physicalCores = CountPhysicalCores();

        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _perfQuery) == 0)
        {
            if (NativeMethods.PdhAddCounter(_perfQuery, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _perfCounter) != 0)
                _perfCounter = IntPtr.Zero;
        }
    }

    private static int CountPhysicalCores()
    {
        try
        {
            uint length = 0;
            // First call: get required buffer size (returns false, sets last
            // error ERROR_INSUFFICIENT_BUFFER = 122).
            NativeMethods.GetLogicalProcessorInformation(IntPtr.Zero, ref length);
            if (length == 0) return Environment.ProcessorCount;

            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!NativeMethods.GetLogicalProcessorInformation(buffer, ref length))
                    return Environment.ProcessorCount;

                int stride = Marshal.SizeOf<NativeMethods.SYSTEM_LOGICAL_PROCESSOR_INFORMATION>();
                int count = (int)(length / stride);
                int cores = 0;
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.SYSTEM_LOGICAL_PROCESSOR_INFORMATION>(
                        IntPtr.Add(buffer, i * stride));
                    if (info.Relationship == (uint)NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                        cores++;
                }
                return cores > 0 ? cores : Environment.ProcessorCount;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return Environment.ProcessorCount;
        }
    }

    public void Update(TimeSpan elapsed)
    {
        UpdateAggregate();
        UpdatePerCore();
        UpdateClock();
    }

    private void UpdateAggregate()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return;

        ulong idleTicks = NativeMethods.FileTimeToUlong(idle);
        ulong totalTicks = NativeMethods.FileTimeToUlong(kernel) + NativeMethods.FileTimeToUlong(user);

        if (_prevTotal != 0 && totalTicks > _prevTotal)
        {
            ulong totalDelta = totalTicks - _prevTotal;
            ulong idleDelta = idleTicks - _prevIdle;
            _aggregateRaw = totalDelta == 0 ? 0 : (1.0 - (double)idleDelta / totalDelta) * 100.0;
            // Smooth toward the new raw sample.
            _aggregateUsage += (_aggregateRaw - _aggregateUsage) * Smoothing;
        }

        _prevTotal = totalTicks;
        _prevIdle = idleTicks;
    }

    private void UpdatePerCore()
    {
        // Per-core usage via NtQuerySystemInformation.
        int infoSize = Marshal.SizeOf<NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        int bufferSize = infoSize * _coreCount;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            uint returnLength;
            int status = NativeMethods.NtQuerySystemInformation(
                NativeMethods.SYSTEM_INFORMATION_CLASS.SystemProcessorPerformanceInformation,
                buffer, (uint)bufferSize, out returnLength);
            if (status == 0)
            {
                for (int i = 0; i < _coreCount; i++)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(
                        IntPtr.Add(buffer, i * infoSize));
                    ulong total = info.KernelTime + info.UserTime;
                    ulong idle = info.IdleTime;
                    if (_prevCoreTotal[i] != 0 && total > _prevCoreTotal[i])
                    {
                        ulong totalDelta = total - _prevCoreTotal[i];
                        ulong idleDelta = idle - _prevCoreIdle[i];
                        _coreUsage[i] = totalDelta == 0 ? 0 : (1.0 - (double)idleDelta / totalDelta) * 100.0;
                    }
                    _prevCoreTotal[i] = total;
                    _prevCoreIdle[i] = idle;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // Per-core frequency via CallNtPowerInformation(ProcessorInformation).
        int ppiSize = Marshal.SizeOf<NativeMethods.PROCESSOR_POWER_INFORMATION>();
        IntPtr ppi = Marshal.AllocHGlobal(ppiSize * _coreCount);
        try
        {
            int ret = NativeMethods.CallNtPowerInformation(
                NativeMethods.POWER_INFORMATION_LEVEL.ProcessorInformation,
                IntPtr.Zero, 0, ppi, (uint)(ppiSize * _coreCount));
            if (ret == 0)
            {
                uint maxSeen = 0;
                for (int i = 0; i < _coreCount; i++)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.PROCESSOR_POWER_INFORMATION>(
                        IntPtr.Add(ppi, i * ppiSize));
                    _coreMhz[i] = info.CurrentMhz;
                    if (info.MaxMhz > maxSeen) maxSeen = info.MaxMhz;
                }
                _maxMhz = maxSeen;
                _currentMhz = _coreMhz.Length > 0 ? (uint)_coreMhz.Average(m => (double)m) : 0;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ppi);
        }
    }

    private void UpdateClock()
    {
        // Dynamic clock via PDH "% Processor Performance" of _Total.
        // CurrentMhz = MaxMhz * pct / 100. Values may exceed MaxMhz under turbo
        // — that is correct, so do NOT clamp.
        bool dynamic = false;
        if (_perfCounter != IntPtr.Zero && _maxMhz > 0)
        {
            try
            {
                if (NativeMethods.PdhCollectQueryData(_perfQuery) == 0)
                {
                    int hr = NativeMethods.PdhGetFormattedCounterValue(
                        _perfCounter, NativeMethods.PDH_FMT_DOUBLE, out _, out var val);
                    if (hr == 0 && val.CStatus == 0)
                    {
                        double pct = val.DoubleValue;
                        _currentMhz = (uint)Math.Round(_maxMhz * pct / 100.0);
                        dynamic = true;
                    }
                }
            }
            catch
            {
                dynamic = false;
            }
        }

        // Fallback: keep the static CallNtPowerInformation value already in
        // _currentMhz (set by UpdatePerCore). Nothing to do if dynamic failed.
        _ = dynamic;
    }

    public IEnumerable<Metric> GetMetrics()
    {
        yield return Metric.Gauge("cpu.aggregate", "CPU", _aggregateUsage, "%");
        yield return Metric.Text("cpu.clock", "Clock", _currentMhz, "MHz");
        yield return Metric.Text("cpu.cores", "Cores", _physicalCores, "");
        yield return Metric.Text("cpu.threads", "Threads", _logicalProcessors, "");
        // Per-core usage exposed for potential future rendering; not shown in v1 tiles.
        for (int i = 0; i < _coreCount; i++)
        {
            yield return Metric.Gauge($"cpu.core.{i}", $"Core {i}", _coreUsage[i], "%");
        }
    }

    public double AggregateUsage => _aggregateUsage;
    public uint CurrentMhz => _currentMhz;
    public uint MaxMhz => _maxMhz;
    public int PhysicalCores => _physicalCores;
    public int LogicalProcessors => _logicalProcessors;
    public IReadOnlyList<double> CoreUsage => _coreUsage;

    public void Dispose()
    {
        if (_perfCounter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_perfCounter);
        if (_perfQuery != IntPtr.Zero) NativeMethods.PdhCloseQuery(_perfQuery);
    }
}
