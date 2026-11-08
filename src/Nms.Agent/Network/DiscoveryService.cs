using System.Net;
using System.Net.NetworkInformation;
using Nms.Shared.Contracts;

namespace Nms.Agent.Network;

/// <summary>A name found for an address and where it came from: "dns", "mdns" or "netbios".</summary>
public sealed record ResolvedName(string Hostname, string Source);

/// <summary>Reverse lookup of one address; null when nothing on the network knows a name for it within the timeout.</summary>
public interface IReverseResolver
{
    Task<ResolvedName?> ResolveAsync(IPAddress address, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// DNS first (the box's own resolver, so the site's zone or the router's DHCP registrations), then, when DNS has no
/// PTR record, mDNS and NetBIOS in parallel since those come from the device itself. mDNS wins a tie because it
/// carries the full name. Each stage gets the same timeout, so a host nobody knows costs at most two timeouts.
/// </summary>
public sealed class HostnameResolver : IReverseResolver
{
    public delegate Task<string?> Lookup(IPAddress address, TimeSpan timeout, CancellationToken ct);

    private readonly Lookup _dns, _mdns, _netbios;

    public HostnameResolver() : this(DnsAsync, NameLookups.MdnsReverseAsync, NameLookups.NetBiosNameAsync) { }

    internal HostnameResolver(Lookup dns, Lookup mdns, Lookup netbios) { _dns = dns; _mdns = mdns; _netbios = netbios; }

    public async Task<ResolvedName?> ResolveAsync(IPAddress address, TimeSpan timeout, CancellationToken ct)
    {
        if (await Safe(_dns, address, timeout, ct) is { } dns) return new(dns, "dns");
        var mdns = Safe(_mdns, address, timeout, ct);
        var nb = Safe(_netbios, address, timeout, ct);
        await Task.WhenAll(mdns, nb);
        if (mdns.Result is { } m) return new(m, "mdns");
        if (nb.Result is { } n) return new(n, "netbios");
        return null;
    }

    private static async Task<string?> Safe(Lookup lookup, IPAddress address, TimeSpan timeout, CancellationToken ct)
    {
        try { return DiscoveryService.CleanHostname(await lookup(address, timeout, ct), address); }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    private static async Task<string?> DnsAsync(IPAddress address, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { return (await Dns.GetHostEntryAsync(address.ToString(), cts.Token)).HostName; }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }   // NXDOMAIN, SERVFAIL, timeout
    }
}

/// <summary>
/// On-demand ping sweep of a CIDR (max /22) followed by an ARP table read, so L2-only hosts that drop ICMP
/// still show up when the agent is on the same VLAN, then a reverse-DNS lookup of every host found. No nmap dependency.
/// </summary>
public sealed class DiscoveryService(ILogger<DiscoveryService> log, IReverseResolver resolver)
{
    /// <summary>Per-stage lookup budget (DNS, then mDNS+NetBIOS). Lookups run 32 at a time, so a /24 full of nameless hosts costs about 16 x this.</summary>
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
            try
            {
                var r = await resolver.ResolveAsync(IPAddress.Parse(h.Address), HostnameTimeout, ct);
                return h with { Hostname = r?.Hostname, HostnameSource = r?.Source };
            }
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
        if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) name = name[..^6];   // mDNS suffix adds nothing
        return name.Length == 0 ? null : name;
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
