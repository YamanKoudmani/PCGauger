using System.Collections.Concurrent;

namespace PCGauger.Infrastructure;

/// <summary>
/// Fixed-capacity ring buffer of double samples with a wall-clock timestamp per
/// sample. Both tiles use one instance each for their sparklines. Built generically
/// (keyed by metric key) so GPU/Disk/Network in v1.5 reuse the same utility.
///
/// Thread-safe: the polling loop writes from a timer thread while the renderer
/// reads from the UI thread.
/// </summary>
public sealed class RollingHistory
{
    private readonly ConcurrentQueue<(DateTimeOffset Timestamp, double Value)> _samples = new();
    private readonly int _capacity;
    private readonly TimeSpan _window;

    public RollingHistory(TimeSpan window)
    {
        _window = window;
        // One sample per ~250ms over the window, with headroom.
        _capacity = Math.Max(8, (int)(window.TotalMilliseconds / 250) + 4);
    }

    public void Push(double value)
    {
        var now = DateTimeOffset.UtcNow;
        _samples.Enqueue((now, value));
        while (_samples.Count > _capacity)
        {
            _samples.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Return samples within the retention window, oldest first. Values are
    /// normalized to 0..1 against the supplied range so the renderer can plot
    /// directly without knowing units.
    /// </summary>
    public IReadOnlyList<(DateTimeOffset Timestamp, double Value)> Snapshot()
    {
        var cutoff = DateTimeOffset.UtcNow - _window;
        var list = new List<(DateTimeOffset, double)>(_samples.Count);
        foreach (var s in _samples)
        {
            if (s.Timestamp >= cutoff)
                list.Add(s);
        }
        return list;
    }

    public int Capacity => _capacity;
}
