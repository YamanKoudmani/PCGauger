using PCGauger.Metrics;

namespace PCGauger.Infrastructure;

/// <summary>
/// Drives all providers on a single timer. Each provider is updated and read
/// inside its own try/catch so one throwing provider cannot take down the
/// window or starve the others. The latest good snapshot is retained per
/// provider so a transient failure just freezes that tile instead of crashing.
/// </summary>
public sealed class MetricPoller : IDisposable
{
    private readonly IMetricProvider[] _providers;
    private readonly TimeSpan _interval;
    private readonly System.Threading.Timer _timer;
    private readonly Dictionary<string, IReadOnlyList<Metric>> _latest = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();

    public MetricPoller(IEnumerable<IMetricProvider> providers, TimeSpan interval)
    {
        _providers = providers.ToArray();
        _interval = interval;
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        // Prime once synchronously so the first frame has data.
        Tick();
        _timer.Change(TimeSpan.Zero, _interval);
    }

    private void Tick()
    {
        if (_cts.IsCancellationRequested) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var p in _providers)
        {
            try
            {
                p.Update(_interval);
                var metrics = p.GetMetrics().ToArray();
                lock (_lock)
                {
                    _latest[p.GetType().Name] = metrics;
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

    /// <summary>Latest good snapshot for a provider type, or empty if never succeeded.</summary>
    public IReadOnlyList<Metric> GetSnapshot(Type providerType)
    {
        lock (_lock)
        {
            return _latest.TryGetValue(providerType.Name, out var m) ? m : Array.Empty<Metric>();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _timer.Dispose();
        _cts.Dispose();
    }
}
