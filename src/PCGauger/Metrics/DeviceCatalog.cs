namespace PCGauger.Metrics;

/// <summary>
/// Hardware categories that support multiple tile instances (one tile per device).
/// Cpu/Ram stay singletons — a system-wide reading is the only sensible semantics.
/// </summary>
public enum MultiInstanceKind
{
    Disk,
    Gpu,
    Network,
}

/// <summary>
/// One selectable hardware device.
/// <paramref name="Id"/> is the stable identity persisted in config keys:
/// Disk = drive letter ("C:"), Gpu = adapter index ("0"), Network = NetworkInterface.Id (GUID).
/// <paramref name="DisplayName"/> is the primary label ("Local Disk (C:)", "NVIDIA GeForce RTX 4070").
/// <paramref name="Detail"/> is secondary info (free space, link speed); may be empty.
/// </summary>
public sealed record DeviceDescriptor(string Id, string DisplayName, string Detail);

/// <summary>
/// Enumerates the selectable devices for one multi-instance kind.
/// <see cref="GetDevices"/> re-enumerates on every call (all three sources are cheap
/// OS queries) so hotplug/unplug is reflected the next time a picker opens.
/// </summary>
public interface IDeviceCatalog
{
    MultiInstanceKind Kind { get; }

    /// <summary>Currently present devices. Never throws; empty list when none found.</summary>
    IReadOnlyList<DeviceDescriptor> GetDevices();

    /// <summary>
    /// The device v1 implicitly displayed (system drive / adapter 0 / default-route NIC).
    /// Used for v1→v2 config migration and as the default for a freshly added tile.
    /// Null when no device is present at all.
    /// </summary>
    string? DefaultDeviceId { get; }
}
