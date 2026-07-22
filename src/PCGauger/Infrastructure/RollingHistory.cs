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
    private int _capacity;
    private TimeSpan _window;

    public RollingHistory(TimeSpan window)
    {
        _window = window;
        // Capacity is sized for the ACTUAL push rate (samples are pushed once per
        // paint frame, ~30/s), not the old 250ms assumption. A safe 40/s margin
        // keeps the ring buffer from silently truncating the window; the
        // timestamp cutoff in Snapshot() is the real limiter.
        _capacity = CapacityFor(window);
    }

    private static int CapacityFor(TimeSpan window) =>
        Math.Max(8, (int)(window.TotalSeconds * 40) + 16);

    /// <summary>Makes the retention window mutable. On shrink, samples older than
    /// the new window are pruned immediately; on grow it simply keeps collecting.
    /// Capacity is recomputed so the buffer can hold the full window.</summary>
    public void SetWindow(TimeSpan window)
    {
        _window = window;
        _capacity = CapacityFor(window);
        var cutoff = DateTimeOffset.UtcNow - _window;
        while (_samples.Count > _capacity) _samples.TryDequeue(out _);
        // Prune anything now outside the (possibly shorter) window.
        while (_samples.TryPeek(out var head) && head.Timestamp < cutoff)
            _samples.TryDequeue(out _);
    }

    public void Push(double value)
    {
        if (!double.IsFinite(value)) return; // never let NaN/Inf poison the spline
        var now = DateTimeOffset.UtcNow;
        _samples.Enqueue((now, value));
        while (_samples.Count > _capacity)
        {
            _samples.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Return samples within the retention window, oldest first. The result is
    /// preceded by the single most recent sample BEFORE the window (when one
    /// exists) so the renderer can interpolate the curve's exact value at the
    /// sliding window boundary instead of popping each time a sample expires.
    /// </summary>
    public IReadOnlyList<(DateTimeOffset, double)> Snapshot()
    {
        var cutoff = DateTimeOffset.UtcNow - _window;
        var list = new List<(DateTimeOffset, double)>(_samples.Count + 1);
        (DateTimeOffset Timestamp, double Value) context = default;
        bool hasContext = false;
        foreach (var s in _samples)
        {
            if (s.Timestamp >= cutoff)
            {
                if (list.Count == 0 && hasContext) list.Add(context);
                list.Add(s);
            }
            else
            {
                context = s; // track the latest pre-window sample
                hasContext = true;
            }
        }
        return list;
    }

    /// <summary>
    /// Returns at most <paramref name="maxPoints"/> samples spanning the window,
    /// oldest first: one pass over the queue, timestamp-filtered, with each
    /// equal-TIME bucket collapsed to its AVERAGE. Buckets are TIME-ANCHORED: a
    /// sample's bucket comes from its timestamp's position within the sliding
    /// window, not its ordinal index — so bucket membership does not reshuffle
    /// on every push (the old index-based scheme re-partitioned all buckets
    /// every frame, making historical points change value after the fact: the
    /// "shimmer"). Emitted timestamps are bucket centers so the renderer's
    /// time-based X mapping places points evenly.
    /// </summary>
    public IReadOnlyList<(DateTimeOffset Timestamp, double Value)> DecimatedSnapshot(int maxPoints)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - _window;
        int count = 0;
        foreach (var s in _samples)
            if (s.Timestamp >= cutoff) count++;
        if (count == 0) return Array.Empty<(DateTimeOffset, double)>();
        if (count <= maxPoints) return Snapshot();

        var sums = new double[maxPoints];
        var counts = new int[maxPoints];
        double windowTicks = _window.Ticks;
        foreach (var s in _samples)
        {
            if (s.Timestamp < cutoff) continue;
            double frac = (s.Timestamp - cutoff).Ticks / windowTicks; // 0..1 within window
            int bucket = (int)(frac * maxPoints);
            if (bucket >= maxPoints) bucket = maxPoints - 1;
            sums[bucket] += s.Value;
            counts[bucket]++;
        }

        var result = new List<(DateTimeOffset, double)>(maxPoints);
        for (int k = 0; k < maxPoints; k++)
        {
            if (counts[k] == 0) continue;
            var center = cutoff.AddTicks((long)((k + 0.5) * windowTicks / maxPoints));
            result.Add((center, sums[k] / counts[k]));
        }
        return result;
    }

    /// <summary>The retention window (mutable via SetWindow) — used by the renderer to time-map the sparkline's X axis.</summary>
    public TimeSpan Window => _window;

    public int Capacity => _capacity;
}
