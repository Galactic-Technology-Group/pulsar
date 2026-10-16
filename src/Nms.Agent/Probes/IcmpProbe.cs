using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Probes;

/// <summary>
/// ICMP echo. When a source address is required (trunked VLAN mode) a raw socket bound to that address is used,
/// which needs CAP_NET_RAW on Linux. Otherwise the portable <see cref="Ping"/> class is used.
/// </summary>
public sealed class IcmpProbe(ILogger<IcmpProbe> log) : IProbe
{
    public CheckType Type => CheckType.Icmp;
    private static int _idCounter = Environment.ProcessId & 0xFFFF;

    public async Task<ProbeOutcome> RunAsync(CheckDefinition check, IPAddress target, IPAddress? source, CancellationToken ct)
    {
        if (source is null || !OperatingSystem.IsLinux())
            return await PingClassAsync(target, check.TimeoutMs, ct);

        try
        {
            return await RawSocketAsync(target, source, check.TimeoutMs, ct);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AccessDenied)
        {
            log.LogWarning("Raw ICMP socket denied (missing CAP_NET_RAW?); falling back to unbound ping for {Target}", target);
            return await PingClassAsync(target, check.TimeoutMs, ct);
        }
    }

    private static async Task<ProbeOutcome> PingClassAsync(IPAddress target, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken: ct);
            return reply.Status == IPStatus.Success
                ? ProbeOutcome.Pass((int)reply.RoundtripTime)
                : ProbeOutcome.Fail(reply.Status.ToString());
        }
        catch (PingException ex)
        {
            return ProbeOutcome.Fail(ex.InnerException?.Message ?? ex.Message);
        }
    }

    private static async Task<ProbeOutcome> RawSocketAsync(IPAddress target, IPAddress source, int timeoutMs, CancellationToken ct)
    {
        var id = (ushort)Interlocked.Increment(ref _idCounter);
        const ushort seq = 1;
        var packet = BuildEchoRequest(id, seq);

        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
        sock.Bind(new IPEndPoint(source, 0));
        sock.ReceiveTimeout = timeoutMs;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        var buffer = new byte[1500];
        var sw = Stopwatch.StartNew();
        await sock.SendToAsync(packet, SocketFlags.None, new IPEndPoint(target, 0), timeoutCts.Token);

        try
        {
            while (true)
            {
                var res = await sock.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeoutCts.Token);
                var n = res.ReceivedBytes;
                if (n < 28) continue;
                var ihl = (buffer[0] & 0x0F) * 4;           // IPv4 header length
                if (n < ihl + 8) continue;
                var type = buffer[ihl];
                var rid = (ushort)(buffer[ihl + 4] << 8 | buffer[ihl + 5]);
                var rseq = (ushort)(buffer[ihl + 6] << 8 | buffer[ihl + 7]);
                if (type == 0 && rid == id && rseq == seq && ((IPEndPoint)res.RemoteEndPoint).Address.Equals(target))
                    return ProbeOutcome.Pass((int)sw.ElapsedMilliseconds);
                if (type == 3 && n >= ihl + 8 + 28)
                {
                    // Destination unreachable carrying our original header
                    var inner = ihl + 8;
                    var innerIhl = (buffer[inner] & 0x0F) * 4;
                    var oid = (ushort)(buffer[inner + innerIhl + 4] << 8 | buffer[inner + innerIhl + 5]);
                    if (oid == id) return ProbeOutcome.Fail($"DestinationUnreachable(code {buffer[ihl + 1]})");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProbeOutcome.Fail("TimedOut");
        }
    }

    private static byte[] BuildEchoRequest(ushort id, ushort seq)
    {
        var payload = new byte[32];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + i % 26);
        var p = new byte[8 + payload.Length];
        p[0] = 8; p[1] = 0;                      // type echo, code 0
        p[4] = (byte)(id >> 8); p[5] = (byte)id;
        p[6] = (byte)(seq >> 8); p[7] = (byte)seq;
        payload.CopyTo(p, 8);
        var sum = Checksum(p);
        p[2] = (byte)(sum >> 8); p[3] = (byte)sum;
        return p;
    }

    private static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        if (data.Length % 2 == 1) sum += (uint)(data[^1] << 8);
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }
}
