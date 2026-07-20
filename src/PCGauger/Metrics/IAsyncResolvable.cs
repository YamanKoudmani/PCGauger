namespace PCGauger.Metrics;

/// <summary>
/// Implemented by providers whose device resolution is deferred off the
/// construction path so the UI thread never blocks on device I/O at startup.
/// <see cref="BeginResolve"/> kicks off the (timeout-bounded, off-thread)
/// resolution; the provider flips <see cref="IMetricProvider"/> state once it
/// lands. Safe to call from the UI thread; never blocks.
/// </summary>
public interface IAsyncResolvable
{
    /// <summary>Begin asynchronous device resolution. Non-blocking.</summary>
    void BeginResolve();
}
