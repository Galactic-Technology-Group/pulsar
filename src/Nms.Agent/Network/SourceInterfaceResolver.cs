using System.Net;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Network;

/// <summary>
/// Picks the local IPv4 address a probe should bind to.
/// Trunked mode: the interface whose VLAN id matches the check's VLAN, else the interface whose subnet contains the target.
/// Routed mode (or no match): null, meaning let the kernel route it.
/// </summary>
public sealed class SourceInterfaceResolver
{
    public static IPAddress? Resolve(VlanMode mode, int? checkVlanId, IPAddress target, IReadOnlyList<InterfaceInfo> interfaces)
    {
        if (interfaces.Count == 0) return null;

        // Prefer a subnet match regardless of mode; it is always the right answer when it exists.
        foreach (var i in interfaces)
        {
            if (IPAddress.TryParse(i.Address, out var local) && SameSubnet(local, target, i.PrefixLength))
                return local;
        }

        if (mode == VlanMode.Trunked && checkVlanId is not null)
        {
            var byVlan = interfaces.FirstOrDefault(i => i.VlanId == checkVlanId);
            if (byVlan is not null && IPAddress.TryParse(byVlan.Address, out var local)) return local;
        }

        return null;
    }

    public static bool SameSubnet(IPAddress a, IPAddress b, int prefixLength)
    {
        if (a.AddressFamily != b.AddressFamily || prefixLength <= 0) return false;
        var ab = a.GetAddressBytes();
        var bb = b.GetAddressBytes();
        if (ab.Length != 4 || prefixLength > 32) return false;
        uint ua = (uint)(ab[0] << 24 | ab[1] << 16 | ab[2] << 8 | ab[3]);
        uint ub = (uint)(bb[0] << 24 | bb[1] << 16 | bb[2] << 8 | bb[3]);
        uint mask = prefixLength == 0 ? 0 : 0xFFFFFFFFu << (32 - prefixLength);
        return (ua & mask) == (ub & mask);
    }
}
