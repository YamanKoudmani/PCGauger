using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCGauger.Metrics;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// Disk metrics via unprivileged APIs:
/// - Capacity/free from <see cref="DriveInfo"/> (a specific volume by default).
/// - Activity from PDH "\LogicalDisk(&lt;letter&gt;)\% Disk Time" (0–100 clamped),
///   throughput from "\LogicalDisk(&lt;letter&gt;)\Disk Read Bytes/sec" and
///   "\LogicalDisk(&lt;letter&gt;)\Disk Write Bytes/sec".
///
/// No admin required. The system drive is used unless a drive letter is given.
///
/// v2 multi-instance: construct with a drive letter ("C:") to bind one tile to
/// one volume. A removed volume (USB unplug) must never crash the poller — when
/// the drive is absent/unready or its counters can't be opened, <see cref="DeviceAvailable"/>
/// is false and the tile reports zeros; it resumes automatically when the drive
/// returns (counters are lazily (re)opened each time the device comes back).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StorageProvider : IMetricProvider
{
    private readonly string _drive;
    private readonly string _pdhInstance; // "C:" form used by LogicalDisk counters

    // PDH query state. Lazily opened so a missing drive at construction time
    // does not throw and does not permanently disable the tile.
    private IntPtr _query;
    private IntPtr _timeCounter;
    private IntPtr _readCounter;
    private IntPtr _writeCounter;
    private bool _countersOpened;

    private ulong _totalBytes;
    private ulong _freeBytes;
    private double _loadPct;
    private double _readBytesPerSec;
    private double _writeBytesPerSec;

    // Defense-in-depth: if a provider is ever disposed outside the poller's
    // Remove fence, Update/GetMetrics early-return instead of touching freed
    // native PDH handles. Set only in Dispose; read on the hot path.
    private int _disposed;

    /// <summary>
    /// True while the drive is present, ready, and its PDH counters are open.
    /// False for a missing/unready volume or when counter setup failed — the
    /// tile then reports zeros and never throws.
    /// </summary>
    public bool DeviceAvailable { get; private set; }

    public StorageProvider(string drive = null!)
    {
        // Normalize to a "C:" form. The parameterless ctor keeps v1 semantics:
        // the system drive (Environment.SystemDirectory root), exactly as before.
        if (string.IsNullOrEmpty(drive))
        {
            drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        }

        // PDH LogicalDisk instances are named "C:" (no backslash, no trailing
        // separator). Strip any trailing backslash and the root "\" form.
        _drive = drive.EndsWith("\\") ? drive.TrimEnd('\\') : drive;
        _pdhInstance = _drive.TrimEnd('\\');
        if (_pdhInstance.Length == 0) _pdhInstance = "C:";

        // Probe availability immediately; counters are opened lazily on first
        // successful update so a not-yet-ready drive doesn't fail construction.
        ProbeAvailability();
    }

    private void ProbeAvailability()
    {
        try
        {
            var di = new DriveInfo(_drive);
            DeviceAvailable = di.IsReady;
        }
        catch
        {
            DeviceAvailable = false;
        }
    }

    /// <summary>
    /// Open the PDH counters for this volume. Returns false (and leaves
    /// DeviceAvailable false) if the drive is gone or counter setup fails.
    /// Safe to call repeatedly; only opens once per successful probe.
    /// </summary>
    private bool EnsureCounters()
    {
        ProbeAvailability();
        if (!DeviceAvailable)
        {
            CloseCounters();
            return false;
        }

        if (_countersOpened) return true;

        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            DeviceAvailable = false;
            return false;
        }

        string timePath = $@"\LogicalDisk({_pdhInstance})\% Disk Time";
        string readPath = $@"\LogicalDisk({_pdhInstance})\Disk Read Bytes/sec";
        string writePath = $@"\LogicalDisk({_pdhInstance})\Disk Write Bytes/sec";

        bool ok = true;
        if (NativeMethods.PdhAddCounter(_query, timePath, IntPtr.Zero, out _timeCounter) != 0)
        { _timeCounter = IntPtr.Zero; ok = false; }
        if (NativeMethods.PdhAddCounter(_query, readPath, IntPtr.Zero, out _readCounter) != 0)
        { _readCounter = IntPtr.Zero; ok = false; }
        if (NativeMethods.PdhAddCounter(_query, writePath, IntPtr.Zero, out _writeCounter) != 0)
        { _writeCounter = IntPtr.Zero; ok = false; }

        if (!ok)
        {
            CloseCounters();
            DeviceAvailable = false;
            return false;
        }

        _countersOpened = true;
        return true;
    }

    private void CloseCounters()
    {
        if (_timeCounter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_timeCounter);
        if (_readCounter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_readCounter);
        if (_writeCounter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_writeCounter);
        if (_query != IntPtr.Zero) NativeMethods.PdhCloseQuery(_query);
        _timeCounter = IntPtr.Zero;
        _readCounter = IntPtr.Zero;
        _writeCounter = IntPtr.Zero;
        _query = IntPtr.Zero;
        _countersOpened = false;
    }

    public void Update(TimeSpan elapsed)
    {
        // Disposed guard: never touch native handles after CloseCounters.
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0) return;

        // Capacity/free from DriveInfo. If the drive vanished, report zeros and
        // mark unavailable; counters are torn down so they reopen on return.
        try
        {
            var di = new DriveInfo(_drive);
            if (di.IsReady)
            {
                _totalBytes = (ulong)di.TotalSize;
                _freeBytes = (ulong)di.AvailableFreeSpace;
            }
            else
            {
                DeviceAvailable = false;
                _totalBytes = 0;
                _freeBytes = 0;
                _loadPct = 0;
                _readBytesPerSec = 0;
                _writeBytesPerSec = 0;
                CloseCounters();
                return;
            }
        }
        catch
        {
            DeviceAvailable = false;
            _totalBytes = 0;
            _freeBytes = 0;
            _loadPct = 0;
            _readBytesPerSec = 0;
            _writeBytesPerSec = 0;
            CloseCounters();
            return;
        }

        if (!EnsureCounters())
        {
            _loadPct = 0;
            _readBytesPerSec = 0;
            _writeBytesPerSec = 0;
            return;
        }

        DeviceAvailable = true;

        if (_query != IntPtr.Zero && NativeMethods.PdhCollectQueryData(_query) == 0)
        {
            if (_timeCounter != IntPtr.Zero &&
                NativeMethods.PdhGetFormattedCounterValue(_timeCounter, NativeMethods.PDH_FMT_DOUBLE, out _, out var tv) == 0)
            {
                // Clamp like the v1 _Total path: % Disk Time can momentarily read
                // above 100 on busy volumes.
                _loadPct = Math.Max(0.0, Math.Min(100.0, tv.DoubleValue));
            }
            if (_readCounter != IntPtr.Zero &&
                NativeMethods.PdhGetFormattedCounterValue(_readCounter, NativeMethods.PDH_FMT_DOUBLE, out _, out var rv) == 0)
                _readBytesPerSec = rv.DoubleValue;
            if (_writeCounter != IntPtr.Zero &&
                NativeMethods.PdhGetFormattedCounterValue(_writeCounter, NativeMethods.PDH_FMT_DOUBLE, out _, out var wv) == 0)
                _writeBytesPerSec = wv.DoubleValue;
        }
    }

    public IEnumerable<Metric> GetMetrics()
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0) yield break;

        yield return Metric.Gauge("disk.load", "Disk", _loadPct, "%");
        yield return Metric.Text("disk.used", "Used", _totalBytes > _freeBytes ? _totalBytes - _freeBytes : 0, "B");
        yield return Metric.Text("disk.total", "Total", _totalBytes, "B");
        yield return Metric.Text("disk.bps", "Activity", (ulong)(_readBytesPerSec + _writeBytesPerSec), "B/s");
        yield return Metric.Text("disk.queue", "Queue", 0.0, "");
        yield return Metric.Text("disk.read", "Read", (ulong)_readBytesPerSec, "B/s");
        yield return Metric.Text("disk.write", "Write", (ulong)_writeBytesPerSec, "B/s");
    }

    public ulong TotalBytes => _totalBytes;
    public ulong FreeBytes => _freeBytes;
    public double BytesPerSec => _readBytesPerSec + _writeBytesPerSec;
    public double ReadBytesPerSec => _readBytesPerSec;
    public double WriteBytesPerSec => _writeBytesPerSec;

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        CloseCounters();
    }
}
