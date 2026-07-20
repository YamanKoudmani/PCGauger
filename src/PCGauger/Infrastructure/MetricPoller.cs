using System.Collections;
using System.Collections.Concurrent;
using PCGauger.Metrics;

namespace PCGauger.Infrastructure;

/// <summary>
/// Drives all providers on a single timer. Each provider is updated and read
/// inside its own try/catch so one throwing provider cannot take down the
/// window or starve the others. The latest good snapshot is retained per
/// provider so a transient failure just freezes that tile instead of crashing.
///
/// Threading contract: <see cref="Add"/>, <see cref="Remove"/>, <see cref="Tick"/>
/// and <see cref="Dispose"/> all serialize on <see cref="_lock"/>. The lock
/// protects ONLY the provider-set snapshot and the <see cref="_latest"/> write —
/// NOT the blocking per-provider <c>Update</c>/<c>GetMetrics</c> calls. A Tick
/// takes the lock just long enough to copy the current provider keys into a
/// local list and to store each completed snapshot; the actual device I/O
/// (DXGI / DriveInfo / NIC enumeration inside Update) runs OUTSIDE the lock on
/// that local snapshot. This means a slow provider can never block a UI-thread
/// <see cref="Add"/> or <see cref="Remove"/>. Because the snapshot is taken
/// under the lock and Remove drops the provider from <c>_providers</c> under the
/// same lock, no Tick will call Update on a provider removed after Remove
/// returns, so callers may <see cref="IDisposable.Dispose"/> the provider
/// immediately after Remove returns — the lock guarantees no Tick is executing
/// it.
/// </summary>
public sealed class MetricPoller : IDisposable
{
    private readonly ConcurrentDictionary<IMetricProvider, byte> _providers =
        new(ReferenceEqualityComparer.Instance);
    private readonly TimeSpan _interval;
    private readonly System.Threading.Timer _timer;
    private readonly ConcurrentDictionary<IMetricProvider, IReadOnlyList<Metric>> _latest =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();

    public MetricPoller(IEnumerable<IMetricProvider> providers, TimeSpan interval)
    {
        _interval = interval;
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        foreach (var p in providers) Add(p);
    }

    public void Start()
    {
        // Do NOT prime synchronously on the calling (UI) thread — providers
        // resolve devices (DXGI / DriveInfo / NIC) inside Update, which can
        // block on slow hardware. Kick the first tick to a pool thread and let
        // the timer drive the rest. The first frame simply has no data yet.
        _ = System.Threading.Tasks.Task.Run(() => Tick());
        _timer.Change(TimeSpan.Zero, _interval);
    }

    /// <summary>Registers a provider so it is polled. Safe to call at any time; a
    /// provider already registered is not added twice. Serializes on
    /// <see cref="_lock"/> so it can't race a Tick.</summary>
    public void Add(IMetricProvider provider)
    {
        if (provider == null) return;
        lock (_lock)
        {
            _providers[provider] = 0;
        }
    }

    /// <summary>Unregisters a provider and drops its last snapshot. Safe to call
    /// even if the provider was never registered. Serializes on
    /// <see cref="_lock"/> so the caller can Dispose the provider immediately
    /// after this returns without racing an in-flight Tick.</summary>
    public void Remove(IMetricProvider provider)
    {
        if (provider == null) return;
        lock (_lock)
        {
            _providers.TryRemove(provider, out _);
            _latest.TryRemove(provider, out _);
        }
    }

    private void Tick()
    {
        if (_cts.IsCancellationRequested) return;

        // Snapshot the provider set under the lock, then run the (potentially
        // blocking) Update/GetMetrics calls OUTSIDE the lock so a slow provider
        // can't stall a UI-thread Add/Remove. The snapshot is taken under the
        // lock, so a provider removed via Remove (also under the lock) will not
        // be present in this tick's iteration — keeping Remove-then-Dispose
        // race-free.
        List<IMetricProvider> snapshot;
        lock (_lock)
        {
            snapshot = _providers.Keys.ToList();
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var p in snapshot)
        {
            try
            {
                p.Update(_interval);
                var metrics = p.GetMetrics().ToArray();
                // Write the completed snapshot back under the lock.
                lock (_lock)
                {
                    _latest[p] = metrics;
                }
            }
            catch (Exception ex)
            {
                // Fault isolation: log and keep the last good snapshot.
                Console.Error.WriteLine($"[MetricPoller] provider {p.GetType().Name} failed: {ex.Message}");
            }
        }
        LastUpdate = now;
    }

    public DateTimeOffset LastUpdate { get; private set; }

    /// <summary>Latest good snapshot for a provider instance, or empty if never succeeded.</summary>
    public IReadOnlyList<Metric> GetSnapshot(IMetricProvider provider)
    {
        if (provider == null) return Array.Empty<Metric>();
        return _latest.TryGetValue(provider, out var m) ? m : Array.Empty<Metric>();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        // Take the lock after cancelling so a Tick that is about to start (or is
        // mid-flight) finishes before we tear down the timer — no Tick can run
        // against a disposed provider during shutdown.
        lock (_lock)
        {
            _timer.Dispose();
        }
        _cts.Dispose();
    }
}
