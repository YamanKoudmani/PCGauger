using System.Net.NetworkInformation;
using PCGauger.Metrics;
using PCGauger.Metrics.Providers;

namespace PCGauger.Metrics.Catalogs;

/// <summary>
/// Enumerates selectable network interfaces for multi-instance NIC tiles.
/// Mirrors the filtering logic in <see cref="NetworkProvider"/> (Up, not
/// Loopback/Tunnel, has a gateway) so the catalog and provider agree on what is
/// selectable. Re-enumerates on every call (cheap OS query). Never throws —
/// returns an empty list on failure.
///
/// Ordering: the default-route interface first (what v1 auto-selected), then the
/// rest alphabetically by name.
/// </summary>
public sealed class NetworkCatalog : IDeviceCatalog
{
    public MultiInstanceKind Kind => MultiInstanceKind.Network;

    public string? DefaultDeviceId
    {
        get
        {
            try
            {
                var nic = NetworkProvider.SelectInterface();
                return nic?.Id;
            }
            catch
            {
                return null;
            }
        }
    }

    public IReadOnlyList<DeviceDescriptor> GetDevices()
    {
        var selectable = new List<NetworkInterface>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!NetworkProvider.IsSelectable(nic)) continue;
                selectable.Add(nic);
            }
        }
        catch
        {
            return new List<DeviceDescriptor>();
        }

        var defaultNic = NetworkProvider.SelectInterface();
        string? defaultId = defaultNic?.Id;

        // Default-route interface first, then alphabetical by name.
        var ordered = selectable
            .OrderBy(nic => nic.Id == defaultId ? 0 : 1)
            .ThenBy(nic => nic.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<DeviceDescriptor>();
        foreach (var nic in ordered)
        {
            string detail = BuildDetail(nic);
            result.Add(new DeviceDescriptor(nic.Id, nic.Name, detail));
        }
        return result;
    }

    private static string BuildDetail(NetworkInterface nic)
    {
        string type = nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet => "Ethernet",
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            _ => nic.NetworkInterfaceType.ToString(),
        };

        string speed = nic.Speed > 0
            ? (nic.Speed >= 1_000_000_000
                ? $"{nic.Speed / 1_000_000_000} Gbps"
                : $"{nic.Speed / 1_000_000} Mbps")
            : string.Empty;

        var parts = new List<string> { type };
        if (!string.IsNullOrEmpty(speed)) parts.Add(speed);
        if (!string.IsNullOrEmpty(nic.Description) && !string.Equals(nic.Description, nic.Name, StringComparison.OrdinalIgnoreCase))
            parts.Add(nic.Description);

        return string.Join(" · ", parts);
    }
}
