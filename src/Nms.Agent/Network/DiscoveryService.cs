using System.Net;
using System.Net.NetworkInformation;
using Nms.Shared.Contracts;

namespace Nms.Agent.Network;

/// <summary>Reverse lookup of one address; null when there is no PTR record or the resolver does not answer in time.</summary>
public interface IReverseResolver
{
    Task<string?> ResolveAsync(IPAddress address, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Uses the box's own resolver (systemd-resolved / the site's DNS), so names come from the site's DNS zone or the router's DHCP registrations.</summary>
public sealed class DnsReverseResolver : IReverseResolver
{
    public async Task<string?> ResolveAsync(IPAddress address, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), cts.Token);
            return DiscoveryService.CleanHostname(entry.HostName, address);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }   // NXDOMAIN, SERVFAIL, timeout
    }
}

/// <summary>
/// On-demand ping sweep of a CIDR (max /22) followed by an ARP table read, so L2-only hosts that drop ICMP
/// still show up when the agent is on the same VLAN, then a reverse-DNS lookup of every host found. No nmap dependency.
/// </summary>
public sealed class DiscoveryService(ILogger<DiscoveryService> log, IReverseResolver resolver)
{
    /// <summary>Per-address PTR lookup budget. Lookups run 32 at a time, so a full /24 of silent resolvers costs about 8 x this.</summary>
    public static readonly TimeSpan HostnameTimeout = TimeSpan.FromSeconds(1);

    public async Task<DiscoveryResult> RunAsync(DiscoveryRequest req, Guid agentId, CancellationToken ct)
    {
        if (!TryParseCidr(req.Cidr, out var network, out var prefix))
            return new DiscoveryResult(req.RequestId, agentId, req.VlanId, Array.Empty<DiscoveredHost>(), "Invalid CIDR");
        if (prefix < 22)
            return new DiscoveryResult(req.RequestId, agentId, req.VlanId, Array.Empty<DiscoveredHost>(), "Prefix must be /22 or smaller");

        var hosts = Enumerate(network, prefix).ToList();
        log.LogInformation("Discovery {Id}: sweeping {Count} addresses in {Cidr}", req.RequestId, hosts.Count, req.Cidr);

        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, DiscoveredHost>();
        using var gate = new SemaphoreSlim(128);
        var tasks = hosts.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(req.TimeoutMs), cancellationToken: ct);
                if (reply.Status == IPStatus.Success)
                    found[ip.ToString()] = new DiscoveredHost(ip.ToString(), null, (int)reply.RoundtripTime);
            }
            catch { /* unreachable */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);

        // Merge ARP entries (hosts that answered ARP but not ICMP, and MACs for the ones that did).
        foreach (var (ip, mac) in ReadArpTable())
        {
            if (!IPAddress.TryParse(ip, out var addr) || !InNetwork(addr, network, prefix)) continue;
            found.AddOrUpdate(ip, new DiscoveredHost(ip, mac, null), (_, existing) => existing with { MacAddress = mac });
        }

        var list = found.Values.OrderBy(h => ToUInt(IPAddress.Parse(h.Address))).ToList();
        list = await ResolveHostnamesAsync(list, ct);
        log.LogInformation("Discovery {Id}: {Hosts} hosts, {Named} with a hostname", req.RequestId, list.Count, list.Count(h => h.Hostname is not null));
        return new DiscoveryResult(req.RequestId, agentId, req.VlanId, list, null);
    }

    /// <summary>Reverse-resolves every host in parallel; a host whose lookup fails keeps a null hostname. Order is preserved.</summary>
    internal async Task<List<DiscoveredHost>> ResolveHostnamesAsync(IReadOnlyList<DiscoveredHost> hosts, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(32);
        var named = await Task.WhenAll(hosts.Select(async h =>
        {
            await gate.WaitAsync(ct);
            try { return h with { Hostname = await resolver.ResolveAsync(IPAddress.Parse(h.Address), HostnameTimeout, ct) }; }
            finally { gate.Release(); }
        }));
        return named.ToList();
    }

    /// <summary>Trims the trailing dot and drops answers that only echo the address back (some resolvers synthesise "1-2-3-4.in-addr.arpa" or the IP itself).</summary>
    public static string? CleanHostname(string? name, IPAddress address)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0 || name.Equals(address.ToString(), StringComparison.OrdinalIgnoreCase)) return null;
        if (name.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase)) return null;
        return name;
    }

    private static IEnumerable<(string Ip, string Mac)> ReadArpTable()
    {
        const string path = "/proc/net/arp";
        if (!OperatingSystem.IsLinux() || !File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && parts[3] != "00:00:00:00:00:00") yield return (parts[0], parts[3]);
        }
    }

    public static bool TryParseCidr(string cidr, out IPAddress network, out int prefix)
    {
        network = IPAddress.None; prefix = 0;
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || !int.TryParse(parts[1], out prefix)) return false;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || prefix is < 0 or > 32) return false;
        var mask = prefix == 0 ? 0 : 0xFFFFFFFFu << (32 - prefix);
        network = FromUInt(ToUInt(ip) & mask);
        return true;
    }

    private static IEnumerable<IPAddress> Enumerate(IPAddress network, int prefix)
    {
        var start = ToUInt(network);
        var count = prefix == 32 ? 1u : 1u << (32 - prefix);
        var first = prefix >= 31 ? start : start + 1;
        var last = prefix >= 31 ? start + count - 1 : start + count - 2;
        for (var a = first; a <= last; a++) yield return FromUInt(a);
    }

    private static bool InNetwork(IPAddress a, IPAddress network, int prefix)
    {
        var mask = prefix == 0 ? 0 : 0xFFFFFFFFu << (32 - prefix);
        return (ToUInt(a) & mask) == ToUInt(network);
    }

    private static uint ToUInt(IPAddress ip) { var b = ip.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
    private static IPAddress FromUInt(uint v) => new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}
