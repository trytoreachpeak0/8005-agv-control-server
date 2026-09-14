using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RIoT.Sdk.Core;

namespace ControlServer.EmergencyDrill;

internal static class RouteVerdicts
{
    internal const string Direct = "DIRECT";
    internal const string DirectLoopback = "DIRECT_LOOPBACK";
    internal const string ViaTun = "VIA_TUN";
    internal const string Unknown = "UNKNOWN";
}

internal sealed class RouteProbe
{
    public string Host { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public List<string> ResolvedAddresses { get; set; } = [];
    public string? ProbedAddress { get; set; }
    public string? NextHop { get; set; }
    public int? InterfaceIndex { get; set; }
    public int? RouteMetric { get; set; }
    public string? InterfaceName { get; set; }
    public string? InterfaceDescription { get; set; }
    public List<string> InterfaceAddresses { get; set; } = [];
    public List<string> TunMarkers { get; set; } = [];
    public string? LookupError { get; set; }
    public string? SystemProxyWouldUse { get; set; }
    public List<string> ProxyEnvironmentVariablesSet { get; set; } = [];
    public string Verdict { get; set; } = RouteVerdicts.Unknown;
}

internal sealed class LatencyProbe
{
    public List<double> RoundTripsMs { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public double? MedianMs { get; set; }
    public double? MaxMs { get; set; }
    public bool AllSucceeded { get; set; }
    public int? CardStatus { get; set; }
    public bool? CardDeviceKeyMatches { get; set; }
}

/// <summary>
/// Where this host's packets to RIoT actually go, and how long a read takes.
/// </summary>
/// <remarks>
/// <c>remote-ops/fleet.md</c> records the control host reaching RIoT through Clash TUN, where probes
/// and request timings cannot be trusted. <c>UseProxy=false</c> does not get past a TUN adapter, so
/// the host that runs the drill must be shown to route to RIoT directly. The route is read with
/// iphlpapi's <c>GetBestRoute</c> -- the same table <c>Find-NetRoute</c> reads -- and the interface is
/// matched by index.
/// </remarks>
internal static class NetworkPreflight
{
    internal const double MaxRoundTripMs = 1000;
    internal const int LatencyReads = 5;

    private static readonly string[] TunInterfaceMarkers = ["clash", "mihomo", "wintun"];

    private static readonly string[] ProxyVariables = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"];

    internal static async Task<RouteProbe> ProbeRouteAsync(Uri baseUrl)
    {
        RouteProbe probe = new()
        {
            Host = Environment.MachineName,
            OsVersion = Environment.OSVersion.VersionString,
            Target = baseUrl.DnsSafeHost
        };

        foreach (string variable in ProxyVariables)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
            {
                probe.ProxyEnvironmentVariablesSet.Add(variable);
            }
        }
        try
        {
            // What a default HttpClient would have used. Recorded for the evidence only: this tool's
            // transport has UseProxy=false. Userinfo is dropped in case the proxy URI carries it.
            Uri? proxy = HttpClient.DefaultProxy.GetProxy(baseUrl);
            probe.SystemProxyWouldUse = proxy is null || proxy == baseUrl
                ? null
                : proxy.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        }
        catch (InvalidOperationException error)
        {
            probe.SystemProxyWouldUse = "unreadable: " + error.GetType().Name;
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(probe.Target, out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(probe.Target);
        }
        catch (SocketException error)
        {
            probe.LookupError = "DNS: " + error.SocketErrorCode;
            return probe;
        }
        probe.ResolvedAddresses = [.. addresses.Select(address => address.ToString())];
        foreach (IPAddress address in addresses.Where(InTunRange))
        {
            probe.TunMarkers.Add("resolved address " + address + " is in 198.18.0.0/15 (Clash fake-ip)");
        }

        IPAddress? target = Array.Find(addresses, address => address.AddressFamily == AddressFamily.InterNetwork)
            ?? Array.Find(addresses, IPAddress.IsLoopback);
        if (target is { AddressFamily: AddressFamily.InterNetwork } && OperatingSystem.IsWindows())
        {
            probe.ProbedAddress = target.ToString();
            ReadBestRoute(probe, target);
        }
        else if (target is null || !IPAddress.IsLoopback(target))
        {
            probe.LookupError = "route lookup is implemented for IPv4 on Windows only";
        }

        probe.Verdict = probe.TunMarkers.Count > 0
            ? RouteVerdicts.ViaTun
            : target is not null && IPAddress.IsLoopback(target)
                ? RouteVerdicts.DirectLoopback
                : probe.NextHop is not null && probe.LookupError is null
                    ? RouteVerdicts.Direct
                    : RouteVerdicts.Unknown;
        return probe;
    }

    internal static async Task<LatencyProbe> ProbeLatencyAsync(DrillRiot riot, string deviceKey)
    {
        LatencyProbe probe = new();
        for (int read = 1; read <= LatencyReads; read++)
        {
            long started = Stopwatch.GetTimestamp();
            using CancellationTokenSource timeout = new(DrillRiot.ReadTimeout);
            try
            {
                // The allowlisted vehicle-card read (GetVehicleCardAsync), straight through the Facade
                // rather than the adapter: the adapter turns a failure into a disconnected card, and a
                // latency probe needs to see the failure.
                VehicleCard card = await riot.Session.Tasks.GetVehicleCardAsync(deviceKey, timeout.Token);
                probe.RoundTripsMs.Add(Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1));
                probe.CardStatus = card.Status;
                probe.CardDeviceKeyMatches = string.Equals(card.DeviceKey, deviceKey, StringComparison.Ordinal);
            }
            catch (Exception error)
            {
                probe.Errors.Add(FormattableString.Invariant(
                    $"read {read}: {error.GetType().Name} after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms"));
            }
        }

        probe.AllSucceeded = probe.Errors.Count == 0 && probe.RoundTripsMs.Count == LatencyReads;
        if (probe.RoundTripsMs.Count > 0)
        {
            double[] sorted = [.. probe.RoundTripsMs.Order()];
            int middle = sorted.Length / 2;
            probe.MedianMs = sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
            probe.MaxMs = sorted[^1];
        }
        return probe;
    }

    private static void ReadBestRoute(RouteProbe probe, IPAddress target)
    {
        uint destination = BitConverter.ToUInt32(target.GetAddressBytes(), 0);
        int status = NativeMethods.GetBestRoute(destination, 0, out NativeMethods.MibIpForwardRow row);
        if (status != 0)
        {
            probe.LookupError = "GetBestRoute returned " + status.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }

        IPAddress nextHop = new(BitConverter.GetBytes(row.NextHop));
        probe.NextHop = nextHop.ToString();
        probe.InterfaceIndex = (int)row.IfIndex;
        probe.RouteMetric = (int)row.Metric1;
        if (InTunRange(nextHop))
        {
            probe.TunMarkers.Add("next hop " + nextHop + " is in 198.18.0.0/15");
        }

        NetworkInterface? adapter = FindInterface((int)row.IfIndex);
        if (adapter is null)
        {
            probe.LookupError = "no network interface has index " + probe.InterfaceIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }
        probe.InterfaceName = adapter.Name;
        probe.InterfaceDescription = adapter.Description;
        foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
        {
            probe.InterfaceAddresses.Add(unicast.Address.ToString());
            if (InTunRange(unicast.Address))
            {
                probe.TunMarkers.Add("interface address " + unicast.Address + " is in 198.18.0.0/15");
            }
        }
        foreach (string marker in TunInterfaceMarkers)
        {
            if (adapter.Name.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                adapter.Description.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                probe.TunMarkers.Add("egress interface '" + adapter.Name + "' / '" + adapter.Description + "' matches '" + marker + "'");
            }
        }
    }

    private static NetworkInterface? FindInterface(int index)
    {
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (adapter.GetIPProperties().GetIPv4Properties()?.Index == index)
                {
                    return adapter;
                }
            }
            catch (NetworkInformationException)
            {
                // An adapter without IPv4 cannot be the egress of an IPv4 route.
            }
        }
        return null;
    }

    /// <summary>198.18.0.0/15: Clash's TUN gateway (198.18.0.1) and its fake-ip pool.</summary>
    private static bool InTunRange(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 198 && bytes[1] is 18 or 19;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct MibIpForwardRow
        {
            public uint Destination;
            public uint Mask;
            public uint Policy;
            public uint NextHop;
            public uint IfIndex;
            public uint Type;
            public uint Proto;
            public uint Age;
            public uint NextHopAs;
            public uint Metric1;
            public uint Metric2;
            public uint Metric3;
            public uint Metric4;
            public uint Metric5;
        }

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetBestRoute(uint destinationAddress, uint sourceAddress, out MibIpForwardRow bestRoute);
    }
}
