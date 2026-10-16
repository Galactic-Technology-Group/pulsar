using System.Net;
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
