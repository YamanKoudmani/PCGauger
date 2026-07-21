using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
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
public sealed class StorageProvider : IMetricProvider, IAsyncResolvable
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
    private int _consecutiveFailures;
    private int _reopenCooldown;

    private ulong _totalBytes;
    private ulong _freeBytes;
    private double _loadPct;
    private double _readBytesPerSec;
    private double _writeBytesPerSec;

    // Defense-in-depth: if a provider is ever disposed outside the poller's
    // Remove fence, Update/GetMetrics early-return instead of touching freed
    // native PDH handles. Set only in Dispose; read on the hot path.
    private int _disposed;

    // Consecutive PDH-collect failures before the tile flips to "unavailable".
    // A transient glitch (one dropped read, a spun-up HDD) stays invisible.
    private const int FailureThreshold = 3;
    // Polls to wait before re-opening counters after a sustained failure, so a
    // genuinely-removed volume isn't re-opened every second (counter churn).
    private const int ReopenCooldownPolls = 5;

    /// <summary>
    /// True while the drive is present, ready, and its PDH counters are open.
    /// False for a missing/unready volume or when counter setup failed — the
    /// tile then reports zeros and never throws.
    /// </summary>
    public bool DeviceAvailable { get; private set; }

    public StorageProvider(string drive = null!)
        : this(drive, false)
    {
    }

    /// <summary>
    /// Deferred-resolution constructor. Normalizes the drive path. Availability is
    /// OPTIMISTIC (true) — we trust the volume until a PDH read actually fails,
    /// rather than gating on <see cref="DriveInfo.IsReady"/> (a blocking media
    /// probe that flaps on power-managed/removable volumes). Counters are opened
    /// lazily by <see cref="Update"/> / <see cref="BeginResolve"/>; no media probe
    /// happens here, so the UI thread is never blocked at startup.
    /// </summary>
    public StorageProvider(string drive, bool deferResolution)
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

        DeviceAvailable = true;
        if (!deferResolution) TryOpenCounters();
    }

    /// <summary>
    /// Best-effort capacity read using <see cref="DriveInfo.TotalSize"/>/
    /// <see cref="DriveInfo.AvailableFreeSpace"/> (volume geometry), NOT
    /// <see cref="DriveInfo.IsReady"/>. Keeps last-good values on any error and
    /// never flips availability — presence is governed by the PDH read in
    /// <see cref="Update"/>.
    /// </summary>
    private void TryReadCapacity()
    {
        try
        {
            var di = new DriveInfo(_drive);
            _totalBytes = (ulong)di.TotalSize;
            _freeBytes = (ulong)di.AvailableFreeSpace;
        }
        catch
        {
            // Drive momentarily unreadable or gone: keep last-good capacity.
        }
    }

    /// <summary>
    /// Open the PDH counters for this volume if not already open. Does NOT probe
    /// DriveInfo first — opening counters is cheap and doesn't touch the media.
    /// On failure the counters are closed and a re-open cooldown starts so a
    /// genuinely-removed volume isn't re-opened every poll.
    /// </summary>
    private bool TryOpenCounters()
    {
        if (_countersOpened) return true;
        if (_reopenCooldown > 0)
        {
            _reopenCooldown--;
            return false;
        }

        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            _reopenCooldown = ReopenCooldownPolls;
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
            _reopenCooldown = ReopenCooldownPolls;
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

        // Lazily open counters. If they can't open yet, count it and back off —
        // availability only flips after a sustained run of failures.
        if (!TryOpenCounters())
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= FailureThreshold)
                DeviceAvailable = false;
            return;
        }

        // Capacity is best-effort and independent of availability.
        TryReadCapacity();

        // The PDH collect is the real presence signal: it succeeds only while the
        // LogicalDisk instance exists. A single failed collect is a transient
        // glitch (keep last-good, leave counters open); a sustained run flips the
        // tile to "unavailable", closes the counters, and starts a re-open
        // cooldown so they re-open on the drive's return without per-poll churn.
        if (_query != IntPtr.Zero && NativeMethods.PdhCollectQueryData(_query) == 0)
        {
            _consecutiveFailures = 0;
            DeviceAvailable = true;

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
        else
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= FailureThreshold)
            {
                DeviceAvailable = false;
                _loadPct = 0;
                _readBytesPerSec = 0;
                _writeBytesPerSec = 0;
                CloseCounters();
                _reopenCooldown = ReopenCooldownPolls;
            }
            // else: transient glitch — keep last-good values, leave counters open.
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

    /// <summary>
    /// Begins asynchronous device resolution (off-thread). Non-blocking; safe to
    /// call from the UI thread. Warms the counters and capacity on a worker
    /// thread. Availability is governed by the PDH collect in <see cref="Update"/>,
    /// which re-runs the same open each poll, so the tile always recovers
    /// regardless of when this lands.
    /// </summary>
    public void BeginResolve()
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0) return;
        Task.Run(() =>
        {
            if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0) return;
            if (TryOpenCounters()) TryReadCapacity();
        });
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        CloseCounters();
    }
}
