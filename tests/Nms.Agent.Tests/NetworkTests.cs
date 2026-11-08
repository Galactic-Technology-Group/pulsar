using System.Net;
using System.Net.Sockets;
using Nms.Agent.Network;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Tests;

public class SourceInterfaceResolverTests
{
    private static readonly InterfaceInfo Eth0 = new("eth0", null, "192.168.1.50", 24);
    private static readonly InterfaceInfo Vlan10 = new("eth0.10", 10, "10.10.0.5", 24);
    private static readonly InterfaceInfo Vlan20 = new("eth0.20", 20, "10.20.0.5", 24);
    private static readonly InterfaceInfo[] All = [Eth0, Vlan10, Vlan20];

    [Fact]
    public void Subnet_match_wins_in_any_mode()
    {
        var src = SourceInterfaceResolver.Resolve(VlanMode.Routed, null, IPAddress.Parse("10.20.0.77"), All);
        Assert.Equal(IPAddress.Parse("10.20.0.5"), src);
    }

    [Fact]
    public void Trunked_uses_vlan_id_when_target_is_outside_every_subnet()
    {
        var src = SourceInterfaceResolver.Resolve(VlanMode.Trunked, 10, IPAddress.Parse("172.16.5.9"), All);
        Assert.Equal(IPAddress.Parse("10.10.0.5"), src);
    }

    [Fact]
    public void Routed_with_no_subnet_match_lets_the_kernel_route()
    {
        Assert.Null(SourceInterfaceResolver.Resolve(VlanMode.Routed, 10, IPAddress.Parse("172.16.5.9"), All));
    }

    [Fact]
    public void Trunked_with_unknown_vlan_returns_null()
    {
        Assert.Null(SourceInterfaceResolver.Resolve(VlanMode.Trunked, 99, IPAddress.Parse("172.16.5.9"), All));
    }

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.254", 24, true)]
    [InlineData("10.0.0.1", "10.0.1.1", 24, false)]
    [InlineData("10.0.0.1", "10.0.1.1", 16, true)]
    [InlineData("10.0.0.0", "10.0.0.1", 31, true)]
    [InlineData("10.0.0.1", "10.0.0.2", 31, false)]
    [InlineData("10.0.0.1", "10.0.0.1", 32, true)]
    public void SameSubnet_masks_correctly(string a, string b, int prefix, bool expected) =>
        Assert.Equal(expected, SourceInterfaceResolver.SameSubnet(IPAddress.Parse(a), IPAddress.Parse(b), prefix));
}

public class InterfaceNameTests
{
    [Theory]
    [InlineData("eth0.10", 10)]
    [InlineData("ens18.4094", 4094)]
    [InlineData("vlan30", 30)]
    [InlineData("VLAN7", 7)]
    [InlineData("eth0", null)]
    [InlineData("wlan0", null)]
    [InlineData("br-lan", null)]
    public void Infers_vlan_from_common_naming(string name, int? expected) =>
        Assert.Equal(expected, InterfaceDiscovery.InferVlanFromName(name));
}

public class CidrTests
{
    [Theory]
    [InlineData("10.20.0.0/24", "10.20.0.0", 24)]
    [InlineData("10.20.0.77/24", "10.20.0.0", 24)]
    [InlineData("192.168.1.130/25", "192.168.1.128", 25)]
    public void Parses_and_normalises_network(string cidr, string network, int prefix)
    {
        Assert.True(DiscoveryService.TryParseCidr(cidr, out var net, out var p));
        Assert.Equal(IPAddress.Parse(network), net);
        Assert.Equal(prefix, p);
    }

    [Theory]
    [InlineData("10.20.0.0")]
    [InlineData("10.20.0.0/33")]
    [InlineData("fe80::1/64")]
    [InlineData("nope/24")]
    public void Rejects_bad_input(string cidr) => Assert.False(DiscoveryService.TryParseCidr(cidr, out _, out _));
}

public class DiscoveryHostnameTests
{
    private sealed class FakeResolver(IDictionary<string, string?> names) : IReverseResolver
    {
        public int Calls;
        public Task<ResolvedName?> ResolveAsync(IPAddress address, TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(names.TryGetValue(address.ToString(), out var n) && n is not null ? new ResolvedName(n, "dns") : null);
        }
    }

    [Fact]
    public async Task Hosts_get_their_reverse_name_and_keep_order()
    {
        var resolver = new FakeResolver(new Dictionary<string, string?> { ["10.0.0.2"] = "printer.site.lan", ["10.0.0.9"] = null });
        var svc = new DiscoveryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<DiscoveryService>.Instance, resolver);
        var input = new List<DiscoveredHost> { new("10.0.0.2", "aa:bb", 3), new("10.0.0.5", null, 1), new("10.0.0.9", "cc:dd", null) };

        var named = await svc.ResolveHostnamesAsync(input, CancellationToken.None);

        Assert.Equal(["10.0.0.2", "10.0.0.5", "10.0.0.9"], named.Select(h => h.Address));
        Assert.Equal("printer.site.lan", named[0].Hostname);
        Assert.Equal("dns", named[0].HostnameSource);
        Assert.Null(named[1].Hostname);
        Assert.Null(named[1].HostnameSource);
        Assert.Null(named[2].Hostname);
        Assert.Equal("aa:bb", named[0].MacAddress);   // other fields untouched
        Assert.Equal(3, resolver.Calls);
    }

    [Theory]
    [InlineData("printer.site.lan.", "printer.site.lan")]
    [InlineData("  switch01 ", "switch01")]
    [InlineData("10.0.0.2", null)]
    [InlineData("2.0.0.10.in-addr.arpa", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Office-Printer.local.", "Office-Printer")]
    public void Cleans_resolver_answers(string? raw, string? expected) =>
        Assert.Equal(expected, DiscoveryService.CleanHostname(raw, IPAddress.Parse("10.0.0.2")));
}

public class HostnameResolverTests
{
    private static HostnameResolver.Lookup Returns(string? v) => (_, _, _) => Task.FromResult(v);
    private static readonly IPAddress Ip = IPAddress.Parse("10.0.0.7");

    [Fact]
    public async Task Dns_wins_and_skips_the_fallbacks()
    {
        var fallbackCalls = 0;
        HostnameResolver.Lookup counting = (_, _, _) => { Interlocked.Increment(ref fallbackCalls); return Task.FromResult<string?>("x"); };
        var r = await new HostnameResolver(Returns("nas.site.lan"), counting, counting).ResolveAsync(Ip, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(new ResolvedName("nas.site.lan", "dns"), r);
        Assert.Equal(0, fallbackCalls);
    }

    [Fact]
    public async Task Mdns_beats_netbios_when_dns_is_silent()
    {
        var r = await new HostnameResolver(Returns(null), Returns("Office-Printer.local."), Returns("OFFICE-PRINTER")).ResolveAsync(Ip, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(new ResolvedName("Office-Printer", "mdns"), r);
    }

    [Fact]
    public async Task Netbios_is_used_last()
    {
        var r = await new HostnameResolver(Returns(null), Returns(null), Returns("DESKTOP-4F2K")).ResolveAsync(Ip, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(new ResolvedName("DESKTOP-4F2K", "netbios"), r);
    }

    [Fact]
    public async Task Nothing_known_and_a_throwing_lookup_give_null()
    {
        HostnameResolver.Lookup throws = (_, _, _) => throw new SocketException();
        var r = await new HostnameResolver(throws, Returns(null), Returns("10.0.0.7")).ResolveAsync(Ip, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Null(r);
    }
}

public class NameLookupParserTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));
    private const string Owner7 = "01 37 01 30 01 30 02 31 30 07 69 6e 2d 61 64 64 72 04 61 72 70 61 00";   // 7.0.0.10.in-addr.arpa
    private const string PtrPrinterLocal = "000c 8001 00000078 000e 07 7072696e746572 05 6c6f63616c 00";       // PTR IN(flush) ttl 120 "printer.local"

    [Fact]
    public void Reverse_name_is_octets_reversed() => Assert.Equal("7.0.0.10.in-addr.arpa", NameLookups.ReverseName(IPAddress.Parse("10.0.0.7")));

    [Fact]
    public void Parses_an_mdns_ptr_answer()
    {
        var msg = Hex("0000 8400 0000 0001 0000 0000 " + Owner7 + " " + PtrPrinterLocal);
        Assert.Equal("printer.local", NameLookups.ParseMdnsPtr(msg, "7.0.0.10.in-addr.arpa"));
    }

    [Fact]
    public void Follows_name_compression_in_the_answer()
    {
        // Question for the owner at offset 12, then an answer whose owner is a pointer (0xC00C) to it.
        var msg = Hex("0000 8400 0001 0001 0000 0000 " + Owner7 + " 000c 0001 c00c " + PtrPrinterLocal);
        Assert.Equal("printer.local", NameLookups.ParseMdnsPtr(msg, "7.0.0.10.in-addr.arpa"));
    }

    [Fact]
    public void Mdns_answer_for_another_address_or_garbage_is_ignored()
    {
        var owner8 = Owner7.Replace("01 37", "01 38");
        Assert.Null(NameLookups.ParseMdnsPtr(Hex("0000 8400 0000 0001 0000 0000 " + owner8 + " " + PtrPrinterLocal), "7.0.0.10.in-addr.arpa"));
        Assert.Null(NameLookups.ParseMdnsPtr(Hex("0000 8400"), "7.0.0.10.in-addr.arpa"));
    }

    [Fact]
    public void Parses_the_workstation_name_from_an_nbstat_reply()
    {
        static string Entry(string name, byte suffix, byte flags) =>
            Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(name.PadRight(15))) + suffix.ToString("x2") + flags.ToString("x2") + "00";
        var name34 = "20" + Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("CKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")) + "00";
        var entries = Entry("WORKGROUP", 0x00, 0x84) + Entry("DESKTOP-4F2K", 0x20, 0x04) + Entry("DESKTOP-4F2K", 0x00, 0x04);
        var msg = Hex("abcd 8400 0000 0001 0000 0000 " + name34 + " 0021 0001 00000000 0040 03 " + entries);
        Assert.Equal("DESKTOP-4F2K", NameLookups.ParseNbstat(msg));
    }

    [Fact]
    public void Nbstat_query_packets_and_garbage_are_rejected()
    {
        Assert.Null(NameLookups.ParseNbstat(Hex("abcd 0000 0001 0000 0000 0000")));   // a query, not a response
        Assert.Null(NameLookups.ParseNbstat(Hex("ab")));
    }
}
