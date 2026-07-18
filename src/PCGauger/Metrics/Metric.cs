namespace PCGauger.Metrics;

/// <summary>
/// A single named, typed metric value. Providers publish immutable snapshots
/// of these on each poll. Keeping the snapshot as plain data (no behavior)
/// lets the renderer and history buffer stay provider-agnostic.
/// </summary>
public sealed record Metric(string Key, string Label, MetricKind Kind, double Value, string? Unit = null)
{
    public static Metric Gauge(string key, string label, double value, string? unit = null)
        => new(key, label, MetricKind.Gauge, value, unit);

    public static Metric Text(string key, string label, double value, string? unit = null)
        => new(key, label, MetricKind.Text, value, unit);
}

public enum MetricKind
{
    /// <summary>0..100 percentage, rendered as a bar.</summary>
    Gauge,
    /// <summary>Arbitrary numeric value with a unit, rendered as text.</summary>
    Text,
}
