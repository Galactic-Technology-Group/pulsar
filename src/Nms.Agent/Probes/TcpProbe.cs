using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Probes;

public sealed class TcpProbe : IProbe
{
    public CheckType Type => CheckType.TcpPort;

    public async Task<ProbeOutcome> RunAsync(CheckDefinition check, IPAddress target, IPAddress? source, CancellationToken ct)
    {
        if (check.Port is null or <= 0 or > 65535) return ProbeOutcome.Fail("InvalidPort");

        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        if (source is not null) sock.Bind(new IPEndPoint(source, 0));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(check.TimeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            await sock.ConnectAsync(new IPEndPoint(target, check.Port.Value), cts.Token);
            sw.Stop();
            try { sock.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
            return ProbeOutcome.Pass((int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProbeOutcome.Fail("TimedOut");
        }
        catch (SocketException ex)
        {
            return ProbeOutcome.Fail(ex.SocketErrorCode.ToString());
        }
    }
}
