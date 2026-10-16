using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Probes;

/// <summary>
/// HTTP(S) GET with expected status and optional body regex. Device certificates are usually self-signed, so
/// certificate validation is disabled: the check answers "is the app up", not "is the cert valid".
/// </summary>
public sealed class HttpProbe : IProbe
{
    public CheckType Type => CheckType.Http;

    public async Task<ProbeOutcome> RunAsync(CheckDefinition check, IPAddress target, IPAddress? source, CancellationToken ct)
    {
        var port = check.Port ?? 80;
        var scheme = port == 443 ? "https" : "http";
        var path = string.IsNullOrEmpty(check.HttpPath) ? "/" : check.HttpPath;
        if (!path.StartsWith('/')) path = "/" + path;
        var uri = new Uri($"{scheme}://{target}:{port}{path}");

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(check.TimeoutMs),
            SslOptions = { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
            ConnectCallback = async (ctx, token) =>
            {
                var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                if (source is not null) sock.Bind(new IPEndPoint(source, 0));
                try
                {
                    await sock.ConnectAsync(ctx.DnsEndPoint, token);
                    return new NetworkStream(sock, ownsSocket: true);
                }
                catch { sock.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(check.TimeoutMs) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("nms-agent/1.0");

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)resp.StatusCode;
            var expected = check.ExpectedHttpStatus;
            var statusOk = expected is null ? status is >= 200 and < 400 : status == expected;
            if (!statusOk) return ProbeOutcome.Fail($"HTTP {status}");

            if (!string.IsNullOrEmpty(check.BodyRegex))
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!Regex.IsMatch(body, check.BodyRegex, RegexOptions.None, TimeSpan.FromSeconds(1)))
                    return ProbeOutcome.Fail($"HTTP {status}, body mismatch");
            }
            return ProbeOutcome.Pass((int)sw.ElapsedMilliseconds, $"HTTP {status}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return ProbeOutcome.Fail("TimedOut"); }
        catch (HttpRequestException ex) { return ProbeOutcome.Fail(ex.HttpRequestError.ToString()); }
        catch (SocketException ex) { return ProbeOutcome.Fail(ex.SocketErrorCode.ToString()); }
    }
}
