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
/// and <see cref="Dispose"/> all serialize on <see cref="_lock"/>. A Tick holds
/// the lock for its entire body (snapshot AND the per-provider Update/GetMetrics
/// loop), so a UI-thread <see cref="Remove"/> blocks until the in-flight Tick
/// finishes. Once <see cref="Remove"/> returns, no Tick holds or will hold that
/// provider, so callers may <see cref="IDisposable.Dispose"/> the provider
/// immediately after Remove returns — the lock guarantees no Tick is executing
/// it. Ticks are short (PDH collects are ms-scale) and removes are rare user
/// clicks, so the brief block is acceptable.
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
        // Prime once synchronously so the first frame has data.
        Tick();
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
        // Hold the lock for the ENTIRE tick (snapshot + per-provider loop) so a
        // Remove on the UI thread cannot dispose a provider mid-iteration. This
        // is the fence that makes "Remove then Dispose" race-free. Monitor is
        // reentrant, so Start()'s synchronous prime call (which runs Tick on the
        // UI thread) is safe even if it ever nests.
        lock (_lock)
        {
            if (_cts.IsCancellationRequested) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var p in _providers.Keys)
            {
                try
                {
                    p.Update(_interval);
                    var metrics = p.GetMetrics().ToArray();
                    _latest[p] = metrics;
                }
                catch (Exception ex)
                {
                    // Fault isolation: log and keep the last good snapshot.
                    Console.Error.WriteLine($"[MetricPoller] provider {p.GetType().Name} failed: {ex.Message}");
                }
            }
            LastUpdate = now;
        }
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
