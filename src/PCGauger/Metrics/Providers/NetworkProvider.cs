using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using PCGauger.Metrics;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// Network throughput for the adapter on the default route (auto-picked).
///
/// Uses the fully-managed System.Net.NetworkInformation API
/// (NetworkInterface.GetAllNetworkInterfaces + GetIPStatistics) rather than
/// GetIfTable2/GetIfEntry2 P/Invoke: the last PDH struct-marshaling bug in this
/// codebase killed the app with an AccessViolation, and MIB_IF_ROW2 is a ~1KB
/// struct with nasty alignment. The managed API is unprivileged and
/// functionally equivalent for byte counters.
///
/// Consumption contract (do not change):
///   InterfaceName   — friendly adapter name, "-" when none
///   DownBytesPerSec — download rate, bytes/sec
///   UpBytesPerSec   — upload rate, bytes/sec
///   metrics: net.down (B/s), net.up (B/s), net.name (string in Unit slot)
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetworkProvider : IMetricProvider
{
    public string InterfaceName { get; private set; } = "-";
    public double DownBytesPerSec { get; private set; }
    public double UpBytesPerSec { get; private set; }

    // Cached selection: the interface id we last sampled, plus the previous
    // absolute byte counters used to compute the delta. Re-evaluated when the
    // chosen interface disappears / goes down (roaming ethernet -> wifi).
    private string? _selectedId;
    private long _prevReceived;
    private long _prevSent;
    private bool _havePrev;

    public void Update(TimeSpan elapsed)
    {
        try
        {
            double elapsedSec = elapsed.TotalSeconds;
            if (elapsedSec <= 0) elapsedSec = 0.25;

            var nic = SelectInterface();
            if (nic is null)
            {
                // No suitable adapter: report "-" and zero rates, but keep the
                // last good name only if we previously had one? Contract says
                // name "-" when none, so reset.
                InterfaceName = "-";
                DownBytesPerSec = 0;
                UpBytesPerSec = 0;
                _selectedId = null;
                _havePrev = false;
                return;
            }

            // Re-selection (roam) or first sample: reset the delta baseline so
            // we don't compute a bogus rate across a counter discontinuity.
            if (_selectedId != nic.Id)
            {
                _selectedId = nic.Id;
                _havePrev = false;
            }

            var stats = nic.GetIPStatistics();
            long received = stats.BytesReceived;
            long sent = stats.BytesSent;

            if (_havePrev)
            {
                long dRecv = received - _prevReceived;
                long dSent = sent - _prevSent;
                // Guard against counter resets (interface flapped -> negative
                // delta). Report 0 for that poll rather than a huge negative.
                DownBytesPerSec = dRecv >= 0 ? dRecv / elapsedSec : 0;
                UpBytesPerSec = dSent >= 0 ? dSent / elapsedSec : 0;
            }
            else
            {
                DownBytesPerSec = 0;
                UpBytesPerSec = 0;
            }

            InterfaceName = nic.Name;
            _prevReceived = received;
            _prevSent = sent;
            _havePrev = true;
        }
        catch
        {
            // Network stack hiccup: leave last good values.
        }
    }

    /// <summary>
    /// Pick the adapter carrying the default route. Among interfaces that are
    /// Up, not Loopback/Tunnel, and have a gateway address: prefer
    /// Ethernet/Wireless80211, tiebreak by highest total bytes transferred.
    /// </summary>
    private static NetworkInterface? SelectInterface()
    {
        NetworkInterface? best = null;
        long bestScore = -1;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch
            {
                continue;
            }

            if (props.GatewayAddresses.Count == 0) continue;

            // Preference: Ethernet (2) > Wireless80211 (1) > other (0).
            int pref = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Ethernet => 2,
                NetworkInterfaceType.Wireless80211 => 1,
                _ => 0,
            };

            long total;
            try
            {
                var s = nic.GetIPStatistics();
                total = (long)s.BytesReceived + s.BytesSent;
            }
            catch
            {
                total = 0;
            }

            // Primary sort by preference, secondary by total bytes. Encode as a
            // single comparable score so we pick the highest-preference adapter
            // with the most traffic.
            long score = pref * long.MaxValue / 4 + total;
            if (score > bestScore)
            {
                bestScore = score;
                best = nic;
            }
        }

        return best;
    }

    public IEnumerable<Metric> GetMetrics()
    {
        yield return Metric.Gauge("net.down", "Down", DownBytesPerSec, "B/s");
        yield return Metric.Gauge("net.up", "Up", UpBytesPerSec, "B/s");
        yield return Metric.Text("net.name", "Interface", 0, InterfaceName);
    }

    public void Dispose()
    {
    }
}
