using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nms.Agent.Network;

/// <summary>
/// Name lookups that do not depend on the site's DNS server, for hosts that advertise their own name:
/// an mDNS reverse (PTR) query on UDP 5353, answered by Apple devices, printers, cameras, speakers and most Linux
/// boxes running Avahi; and a NetBIOS node-status query on UDP 137, answered by Windows PCs, NAS boxes and printers.
/// Both are a single UDP exchange with no dependencies. Packet parsing is separated out so it can be unit-tested.
/// </summary>
public static class NameLookups
{
    private static readonly IPEndPoint MdnsGroup = new(IPAddress.Parse("224.0.0.251"), 5353);
    private const ushort TypePtr = 12, TypeNbstat = 0x21, ClassIn = 1;

    // ---- mDNS ------------------------------------------------------------------------------------

    /// <summary>Reverse name for <paramref name="ip"/> via mDNS, or null. The query goes to the multicast group and, for hosts behind a router, unicast to the host itself; either copy may answer.</summary>
    public static async Task<string?> MdnsReverseAsync(IPAddress ip, TimeSpan timeout, CancellationToken ct)
    {
        var qname = ReverseName(ip);
        var query = BuildQuery(id: 0, qname, TypePtr, ClassIn, unicastResponse: true);
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            udp.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            await udp.SendAsync(query, MdnsGroup, ct);
            await udp.SendAsync(query, new IPEndPoint(ip, 5353), ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var name = ParseMdnsPtr(r.Buffer, qname);
                if (name is not null) return name;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>Returns the PTR target for <paramref name="qname"/> from a DNS/mDNS response, or null.</summary>
    public static string? ParseMdnsPtr(ReadOnlySpan<byte> msg, string qname)
    {
        if (msg.Length < 12) return null;
        int qd = msg[4] << 8 | msg[5], an = msg[6] << 8 | msg[7];
        var pos = 12;
        for (var i = 0; i < qd; i++) { if (!SkipName(msg, ref pos)) return null; pos += 4; }
        for (var i = 0; i < an; i++)
        {
            var owner = ReadName(msg, ref pos);
            if (owner is null || pos + 10 > msg.Length) return null;
            int type = msg[pos] << 8 | msg[pos + 1], rdlen = msg[pos + 8] << 8 | msg[pos + 9];
            pos += 10;
            if (type == TypePtr && owner.Equals(qname, StringComparison.OrdinalIgnoreCase))
            {
                var p = pos;
                var target = ReadName(msg, ref p);
                if (!string.IsNullOrEmpty(target)) return target;
            }
            pos += rdlen;
            if (pos > msg.Length) return null;
        }
        return null;
    }

    public static string ReverseName(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
    }

    // ---- NetBIOS -------------------------------------------------------------------------------------

    /// <summary>Workstation name from a NetBIOS node-status (NBSTAT) reply, or null.</summary>
    public static async Task<string?> NetBiosNameAsync(IPAddress ip, TimeSpan timeout, CancellationToken ct)
    {
        // The wildcard name "*" padded to 16 bytes, first-level encoded: "CK" followed by 30 'A's.
        var query = BuildQuery(id: (ushort)Random.Shared.Next(1, 0xFFFF), "CKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", TypeNbstat, ClassIn, unicastResponse: false);
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(query, new IPEndPoint(ip, 137), ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var r = await udp.ReceiveAsync(cts.Token);
            return ParseNbstat(r.Buffer);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>
    /// Picks the unique (non-group) name with suffix 0x00, which is the machine's workstation name, from an NBSTAT reply.
    /// Layout after the 12-byte header: 34-byte name, type(2), class(2), ttl(4), rdlength(2), count(1), then 18-byte entries
    /// of 15 name chars + suffix + 2 flag bytes; bit 0x80 of the first flag byte marks a group name.
    /// </summary>
    public static string? ParseNbstat(ReadOnlySpan<byte> msg)
    {
        if (msg.Length < 12 || (msg[2] & 0x80) == 0) return null;   // not a response
        var pos = 12;
        if (!SkipName(msg, ref pos) || pos + 11 > msg.Length) return null;
        int type = msg[pos] << 8 | msg[pos + 1];
        if (type != TypeNbstat) return null;
        pos += 10;
        int count = msg[pos++];
        string? fallback = null;
        for (var i = 0; i < count && pos + 18 <= msg.Length; i++, pos += 18)
        {
            var name = Encoding.ASCII.GetString(msg.Slice(pos, 15)).TrimEnd(' ', '\0');
            var suffix = msg[pos + 15];
            var group = (msg[pos + 16] & 0x80) != 0;
            if (group || name.Length == 0 || name.StartsWith('\x01')) continue;   // skip groups and the __MSBROWSE__ entry
            if (suffix == 0x00) return name;
            fallback ??= suffix == 0x20 ? name : null;   // file-server name is the same machine name on nearly every host
        }
        return fallback;
    }

    // ---- shared DNS wire helpers -------------------------------------------------------------------------------

    private static byte[] BuildQuery(ushort id, string qname, ushort qtype, ushort qclass, bool unicastResponse)
    {
        var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        U16(id); U16(0); U16(1); U16(0); U16(0); U16(0);
        foreach (var label in qname.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length); ms.Write(bytes);
        }
        ms.WriteByte(0);
        U16(qtype); U16(unicastResponse ? qclass | 0x8000 : qclass);
        return ms.ToArray();
    }

    private static bool SkipName(ReadOnlySpan<byte> msg, ref int pos)
    {
        while (pos < msg.Length)
        {
            var len = msg[pos];
            if (len == 0) { pos++; return true; }
            if ((len & 0xC0) == 0xC0) { pos += 2; return true; }
            pos += 1 + len;
        }
        return false;
    }

    /// <summary>Reads a possibly compressed name; advances <paramref name="pos"/> past it in the original stream.</summary>
    private static string? ReadName(ReadOnlySpan<byte> msg, ref int pos)
    {
        var labels = new List<string>();
        var p = pos; var jumped = false; var hops = 0;
        while (true)
        {
            if (p >= msg.Length || ++hops > 64) return null;
            var len = msg[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= msg.Length) return null;
                var ptr = (len & 0x3F) << 8 | msg[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true; p = ptr; continue;
            }
            if (p + 1 + len > msg.Length) return null;
            labels.Add(Encoding.ASCII.GetString(msg.Slice(p + 1, len)));
            p += 1 + len;
        }
        if (!jumped) pos = p;
        return string.Join('.', labels);
    }
}
