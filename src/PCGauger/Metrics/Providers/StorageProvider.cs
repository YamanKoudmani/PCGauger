using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCGauger.Metrics;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// Disk metrics via unprivileged APIs:
/// - Capacity/free from GetDiskFreeSpaceEx (system drive by default).
/// - Activity from PDH "\PhysicalDisk(_Total)\Disk Bytes/sec" and
///   "\PhysicalDisk(_Total)\Avg. Disk Queue Length".
///
/// No admin required. The system drive is used unless overridden.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StorageProvider : IMetricProvider
{
    private readonly string _drive;
    private IntPtr _query;
    private IntPtr _bytesCounter;
    private IntPtr _queueCounter;
    private IntPtr _readCounter;
    private IntPtr _writeCounter;
    private ulong _totalBytes;
    private ulong _freeBytes;
    private double _bytesPerSec;
    private double _avgQueue;
    private double _readBytesPerSec;
    private double _writeBytesPerSec;

    public StorageProvider(string drive = null!)
    {
        _drive = string.IsNullOrEmpty(drive) ? Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\" : drive;
        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _query) == 0)
        {
            if (NativeMethods.PdhAddCounter(_query, @"\PhysicalDisk(_Total)\Disk Bytes/sec", IntPtr.Zero, out _bytesCounter) != 0)
                _bytesCounter = IntPtr.Zero;
            if (NativeMethods.PdhAddCounter(_query, @"\PhysicalDisk(_Total)\Avg. Disk Queue Length", IntPtr.Zero, out _queueCounter) != 0)
                _queueCounter = IntPtr.Zero;
            if (NativeMethods.PdhAddCounter(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _readCounter) != 0)
                _readCounter = IntPtr.Zero;
            if (NativeMethods.PdhAddCounter(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _writeCounter) != 0)
                _writeCounter = IntPtr.Zero;
        }
    }

    public void Update(TimeSpan elapsed)
    {
        if (NativeMethods.GetDiskFreeSpaceEx(_drive, out _, out _totalBytes, out _freeBytes))
        {
            // values already set
        }

        if (_query != IntPtr.Zero && NativeMethods.PdhCollectQueryData(_query) == 0)
        {
            if (_bytesCounter != IntPtr.Zero &&
                NativeMethods.PdhGetFormattedCounterValue(_bytesCounter, NativeMethods.PDH_FMT_DOUBLE, out _, out var bv) == 0)
                _bytesPerSec = bv.DoubleValue;
            if (_queueCounter != IntPtr.Zero &&
                NativeMethods.PdhGetFormattedCounterValue(_queueCounter, NativeMethods.PDH_FMT_DOUBLE, out _, out var qv) == 0)
                _avgQueue = qv.DoubleValue;
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
        ulong used = _totalBytes > _freeBytes ? _totalBytes - _freeBytes : 0;
        double pct = _totalBytes > 0 ? (double)used / _totalBytes * 100.0 : 0;
        yield return Metric.Gauge("disk.load", "Disk", pct, "%");
        yield return Metric.Text("disk.used", "Used", used, "B");
        yield return Metric.Text("disk.total", "Total", _totalBytes, "B");
        yield return Metric.Text("disk.bps", "Activity", (ulong)_bytesPerSec, "B/s");
        yield return Metric.Text("disk.queue", "Queue", _avgQueue, "");
        yield return Metric.Text("disk.read", "Read", (ulong)_readBytesPerSec, "B/s");
        yield return Metric.Text("disk.write", "Write", (ulong)_writeBytesPerSec, "B/s");
    }

    public ulong TotalBytes => _totalBytes;
    public ulong FreeBytes => _freeBytes;
    public double BytesPerSec => _bytesPerSec;
    public double ReadBytesPerSec => _readBytesPerSec;
    public double WriteBytesPerSec => _writeBytesPerSec;

    public void Dispose()
    {
        if (_bytesCounter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_bytesCounter);
        if (_queueCounter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_queueCounter);
        if (_readCounter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_readCounter);
        if (_writeCounter != IntPtr.Zero) NativeMethods.PdhRemoveCounter(_writeCounter);
        if (_query != IntPtr.Zero) NativeMethods.PdhCloseQuery(_query);
    }
}
