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
/// protects the provider-set snapshot and the <see cref="_latest"/> write.
///
/// Per-provider isolation: a <see cref="Tick"/> does NOT run providers serially.
/// It launches each provider's <c>Update</c>/<c>GetMetrics</c> on its OWN pool
/// task and returns immediately, so a slow or briefly-blocking provider
/// (process enumeration, HDD spin-up, a wedged PDH/DXGI call) runs at its own
/// pace and can never starve the other providers — the original serial loop let
/// one ~30s <c>TopProcessProvider</c> freeze every tile at startup. A
/// per-provider re-entrancy flag skips launching a new update while that
/// provider's previous one is still running, so a provider's <c>Update</c> never
/// overlaps itself.
///
/// Remove-then-Dispose safety: <see cref="Remove"/> drains (bounded) the
/// provider's in-flight update before returning, so the caller may
/// <see cref="IDisposable.Dispose"/> the provider immediately after Remove
/// returns without a use-after-free on its native handles. Providers also keep
/// their own <c>_disposed</c> guards as a second line of defense.
/// </summary>
public sealed class MetricPoller : IDisposable
{
    private readonly ConcurrentDictionary<IMetricProvider, byte> _providers =
        new(ReferenceEqualityComparer.Instance);
    private readonly TimeSpan _interval;
    private readonly System.Threading.Timer _timer;
    private readonly ConcurrentDictionary<IMetricProvider, IReadOnlyList<Metric>> _latest =
        new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<IMetricProvider, ProviderState> _states =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Per-provider scheduling state. <see cref="Updating"/> is 0 when
    /// idle and 1 while an update task is launched/running; it is set before the
    /// task starts and cleared in the task's <c>finally</c>, so it doubles as the
    /// drain signal <see cref="Remove"/> waits on.</summary>
    private sealed class ProviderState
    {
        public int Updating;
    }

    public MetricPoller(IEnumerable<IMetricProvider> providers, TimeSpan interval)
    {
        _interval = interval;
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        foreach (var p in providers) Add(p);
    }

    public void Start()
    {
        // Single source of ticks: the timer only. Each provider update already runs
        // on its own pool task (see Tick), so an immediate first tick never blocks
        // the UI thread — the old Task.Run(Tick) + TimeSpan.Zero combination
        // double-fired the first tick and raced every provider's first Update.
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

    /// <summary>Unregisters a provider, drops its last snapshot, and drains any
    /// in-flight update before returning. Safe to call even if the provider was
    /// never registered. After this returns the caller may Dispose the provider
    /// without racing an in-flight Update. The drain is bounded so a hung provider
    /// can't freeze the UI thread on the rebind path.</summary>
    public void Remove(IMetricProvider provider)
    {
        if (provider == null) return;
        ProviderState? st = null;
        lock (_lock)
        {
            _providers.TryRemove(provider, out _);
            _latest.TryRemove(provider, out _);
            _states.TryGetValue(provider, out st);
        }

        // Wait (bounded) for the provider's update task to finish. The Updating
        // flag is set before the task launches and cleared in its finally, so it
        // covers tasks launched just before removal. Fast providers return almost
        // instantly; the bound prevents a stuck provider from blocking the UI.
        if (st != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (System.Threading.Interlocked.CompareExchange(ref st.Updating, 0, 0) != 0
                   && sw.ElapsedMilliseconds < 3000)
            {
                System.Threading.Thread.Sleep(5);
            }
        }
    }

    private void Tick()
    {
        if (_cts.IsCancellationRequested) return;

        // Snapshot the provider set under the lock, then launch each provider's
        // update on its own task. A provider removed via Remove (under the lock)
        // is not present in this snapshot.
        List<IMetricProvider> snapshot;
        lock (_lock)
        {
            snapshot = _providers.Keys.ToList();
        }
        LastUpdate = DateTimeOffset.UtcNow;

        foreach (var p in snapshot)
        {
            var state = _states.GetOrAdd(p, _ => new ProviderState());

            // Skip if this provider's previous update is still running — it runs
            // at its own pace and never blocks the others.
            if (System.Threading.Interlocked.CompareExchange(ref state.Updating, 1, 0) != 0)
                continue;

            // Re-check registration under the lock so a provider removed between
            // the snapshot and now is not launched (Remove drains via Updating).
            lock (_lock)
            {
                if (!_providers.ContainsKey(p))
                {
                    System.Threading.Interlocked.Exchange(ref state.Updating, 0);
                    continue;
                }
            }

            _ = System.Threading.Tasks.Task.Run(() =>
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
                finally
                {
                    System.Threading.Interlocked.Exchange(ref state.Updating, 0);
                }
            });
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
        // mid-flight) finishes before we tear down the timer.
        lock (_lock)
        {
            _timer.Dispose();
        }
        _cts.Dispose();
    }
}
