using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using OmniSpec.Core.Live;

namespace OmniSpec.Monitoring.Providers;

/// <summary>
/// Network throughput from interface byte counters (IP Helper API). Purely local: nothing is sent anywhere.
/// The adapter list is refreshed every few seconds; byte counters are read on every sample.
/// "Connection uptime" is the time since OmniSpec observed the adapter come up (Windows does not expose link-up time).
/// </summary>
public sealed class NetworkProvider : INetworkProvider
{
    private sealed class State
    {
        public long LastRx, LastTx, StartRx, StartTx;
        public long LastTicks;
        public DateTimeOffset? UpSince;
        public bool WasUp;
    }

    private readonly Dictionary<string, State> _state = new();
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private NetworkInterface[] _interfaces = Array.Empty<NetworkInterface>();
    private long _lastListRefresh;

    public IReadOnlyList<NetworkAdapterSample> Sample()
    {
        var nowTicks = Stopwatch.GetTimestamp();
        if (_interfaces.Length == 0 || Stopwatch.GetElapsedTime(_lastListRefresh, nowTicks) > TimeSpan.FromSeconds(5))
        {
            try { _interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { _interfaces = Array.Empty<NetworkInterface>(); }
            _lastListRefresh = nowTicks;
        }

        var result = new List<NetworkAdapterSample>(_interfaces.Length);
        foreach (var ni in _interfaces)
        {
            try
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || IsFilterPseudoInterface(ni.Name)) continue;
                var stats = ni.GetIPStatistics();
                var rx = stats.BytesReceived;
                var tx = stats.BytesSent;
                var up = ni.OperationalStatus == OperationalStatus.Up;

                if (!_state.TryGetValue(ni.Id, out var s))
                {
                    s = new State { LastRx = rx, LastTx = tx, StartRx = rx, StartTx = tx, LastTicks = nowTicks, WasUp = up };
                    _state[ni.Id] = s;
                }

                var seconds = Stopwatch.GetElapsedTime(s.LastTicks, nowTicks).TotalSeconds;
                double down = 0, upRate = 0;
                if (seconds > 0.05)
                {
                    down = Math.Max(0, rx - s.LastRx) / seconds;
                    upRate = Math.Max(0, tx - s.LastTx) / seconds;
                }
                // Counters reset when an adapter reconnects; restart the session baseline.
                if (rx < s.LastRx || tx < s.LastTx) { s.StartRx = rx; s.StartTx = tx; }
                if (up && !s.WasUp) s.UpSince = DateTimeOffset.Now;
                if (!up) s.UpSince = null;
                s.WasUp = up;
                s.LastRx = rx; s.LastTx = tx; s.LastTicks = nowTicks;

                long? speed = ni.Speed > 0 && ni.Speed < long.MaxValue / 2 ? ni.Speed : null;
                IReadOnlyList<string> ips;
                try
                {
                    ips = ni.GetIPProperties().UnicastAddresses
                        .Where(a => a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                        .Select(a => a.Address.ToString()).ToList();
                }
                catch { ips = Array.Empty<string>(); }

                var mac = ni.GetPhysicalAddress().GetAddressBytes();
                var description = ni.Description ?? string.Empty;
                var isVirtual = ni.NetworkInterfaceType is NetworkInterfaceType.Tunnel ||
                                description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                                description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                                description.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
                                description.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
                                description.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) ||
                                description.Contains("Filter", StringComparison.OrdinalIgnoreCase);

                result.Add(new NetworkAdapterSample(
                    ni.Id, ni.Name, description, ni.NetworkInterfaceType.ToString(), ni.OperationalStatus.ToString(), speed,
                    down, upRate, rx, tx, rx - s.StartRx, tx - s.StartTx,
                    mac.Length > 0 ? string.Join(":", mac.Select(b => b.ToString("X2"))) : null,
                    ips,
                    s.UpSince ?? (up ? (DateTimeOffset?)null : null),
                    isVirtual));
            }
            catch
            {
                // Adapter disappeared between enumeration and sampling; skip it this round.
            }
        }

        return result
            .OrderByDescending(a => a.Status == "Up")
            .ThenBy(a => a.IsVirtualOrLoopback)
            .ThenByDescending(a => a.TotalBytesReceived)
            .ToList();
    }

    /// <summary>
    /// NDIS lightweight filters (WFP, QoS, Npcap, Native WiFi) appear as extra interfaces such as
    /// "Ethernet-WFP Native MAC Layer LightWeight Filter-0000". They duplicate the real adapter's counters.
    /// </summary>
    public static bool IsFilterPseudoInterface(string name) =>
        name.Contains("Filter-0", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("-WFP ", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("-QoS Packet Scheduler", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("-Npcap Packet Driver", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("-Native WiFi Filter", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("-Virtual WiFi Filter", StringComparison.OrdinalIgnoreCase);

    /// <summary>Time since OmniSpec started, used to qualify "observed" uptime values.</summary>
    public DateTimeOffset ObservationStart => _started;
}
