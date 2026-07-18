namespace PCGauger.Metrics;

/// <summary>
/// A metric source. Implementations accumulate raw state in <see cref="Update"/>
/// and expose an immutable snapshot via <see cref="GetMetrics"/>. The polling
/// loop calls Update on a timer, then GetMetrics once per frame for rendering.
///
/// The interface is intentionally tiny so v1.5 providers (GPU/Disk/Network)
/// slot in without touching the loop or renderer.
/// </summary>
public interface IMetricProvider
{
    /// <summary>Advance internal state. <paramref name="elapsed"/> is the time since the previous Update.</summary>
    void Update(TimeSpan elapsed);

    /// <summary>Return the current immutable snapshot of metrics.</summary>
    IEnumerable<Metric> GetMetrics();
}
