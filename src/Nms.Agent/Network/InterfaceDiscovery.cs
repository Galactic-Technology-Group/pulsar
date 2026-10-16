using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Nms.Shared.Contracts;

namespace Nms.Agent.Network;

/// <summary>
/// Enumerates local IPv4 interfaces and infers their VLAN id. On Linux the 802.1Q sub-interface name
/// convention (eth0.10, ens18.20, vlan30) is used first, then /proc/net/vlan/config as the authority.
/// </summary>
public sealed partial class InterfaceDiscovery(IOptions<AgentOptions> options, ILogger<InterfaceDiscovery> log)
{
    [GeneratedRegex(@"^(?<parent>[a-z0-9]+)\.(?<vlan>\d{1,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex DottedVlan();

    [GeneratedRegex(@"^vlan(?<vlan>\d{1,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex NamedVlan();

    public IReadOnlyList<InterfaceInfo> Enumerate()
    {
        var wanted = options.Value.Interfaces;
        var procVlans = ReadProcVlanConfig();
        var list = new List<InterfaceInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (wanted.Count > 0 && !wanted.Contains(nic.Name, StringComparer.OrdinalIgnoreCase)) continue;

            int? vlan = procVlans.TryGetValue(nic.Name, out var v) ? v : InferVlanFromName(nic.Name);

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                list.Add(new InterfaceInfo(nic.Name, vlan, ua.Address.ToString(), ua.PrefixLength));
            }
        }

        log.LogDebug("Discovered {Count} interface addresses", list.Count);
        return list;
    }

    public static int? InferVlanFromName(string name)
    {
        var m = DottedVlan().Match(name);
        if (m.Success) return int.Parse(m.Groups["vlan"].Value);
        m = NamedVlan().Match(name);
        if (m.Success) return int.Parse(m.Groups["vlan"].Value);
        return null;
    }

    /// <summary>Parses /proc/net/vlan/config: "eth0.10 | 10 | eth0".</summary>
    private static Dictionary<string, int> ReadProcVlanConfig()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        const string path = "/proc/net/vlan/config";
        if (!OperatingSystem.IsLinux() || !File.Exists(path)) return result;
        try
        {
            foreach (var line in File.ReadLines(path).Skip(2))
            {
                var parts = line.Split('|', StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out var id)) result[parts[0]] = id;
            }
        }
        catch { /* best effort */ }
        return result;
    }
}
