using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;

namespace Nms.Worker.Services;

/// <summary>Outcome of one outside-in probe. Reachable == null means the probe could not run at all (e.g. no ICMP permission).</summary>
public sealed record GatewayProbeResult(bool? Reachable, int? LatencyMs, string? Detail);

public interface IGatewayPinger
{
    Task<GatewayProbeResult> ProbeAsync(string address, int? port, int timeoutMs, CancellationToken ct);
}

/// <summary>
/// ICMP echo from the worker's own network, or, when a port is set, a TCP connect where a refused connection still
/// proves the host answers. ICMP needs CAP_NET_RAW in the container (the worker Dockerfile grants it to dotnet).
/// </summary>
public sealed class IcmpGatewayPinger : IGatewayPinger
{
    public async Task<GatewayProbeResult> ProbeAsync(string address, int? port, int timeoutMs, CancellationToken ct)
    {
        IPAddress? ip = IPAddress.TryParse(address, out var parsed) ? parsed : null;
        if (ip is null)
        {
            try { ip = (await Dns.GetHostAddressesAsync(address, AddressFamily.InterNetwork, ct)).FirstOrDefault(); }
            catch (SocketException) { ip = null; }
            if (ip is null) return new(false, null, "DnsFailure");
        }

        if (port is { } p) return await TcpAsync(ip, p, timeoutMs, ct);

        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken: ct);
            return reply.Status == IPStatus.Success ? new(true, (int)reply.RoundtripTime, null) : new(false, null, reply.Status.ToString());
        }
        catch (PingException ex)
        {
            // Raw socket refused or no ping helper available: the environment cannot ping, which is not the gateway's fault.
            return new(null, null, "IcmpUnavailable: " + (ex.InnerException?.Message ?? ex.Message));
        }
        catch (PlatformNotSupportedException ex) { return new(null, null, "IcmpUnavailable: " + ex.Message); }
    }

    private static async Task<GatewayProbeResult> TcpAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(ip, port, timeout.Token);
            return new(true, (int)sw.ElapsedMilliseconds, $"tcp/{port} open");
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.ConnectionReset)
        {
            // A RST comes from the host itself: it is up, the port is just closed.
            return new(true, (int)sw.ElapsedMilliseconds, $"tcp/{port} refused (host answers)");
        }
        catch (SocketException ex) { return new(false, null, ex.SocketErrorCode.ToString()); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, null, "TimedOut"); }
    }
}

/// <summary>
/// Probes every site's Gateway device from Azure on its own interval, records the samples like agent results
/// (AgentId = <see cref="GatewayCheck.AzureProbeId"/>), runs the same state machine, and opens/closes GatewayDown
/// incidents. This is the outside-in half of the agent-offline cross-check.
/// </summary>
public sealed class GatewayProbeService(IServiceScopeFactory scopes, IGatewayPinger pinger, IOptions<WorkerOptions> options, ILogger<GatewayProbeService> log) : BackgroundService
{
    private readonly SemaphoreSlim _parallel = new(16);
    private DateTimeOffset _lastUnavailableLog = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Gateway probe pass failed"); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.GatewayPollSec), ct);
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
        var now = DateTimeOffset.UtcNow;

        var due = await db.Checks.Include(c => c.Device).ThenInclude(d => d.Site)
            .Where(c => c.Type == CheckType.GatewayPing && c.Enabled && c.Device.Kind == DeviceKind.Gateway && c.Device.Address != "")
            .ToListAsync(ct);
        // A check is due when its interval has elapsed since its last sample (one second of slack so a 30 s interval runs every pass that lands on it).
        due = due.Where(c => c.LastResultUtc is null || c.LastResultUtc.Value.AddSeconds(c.IntervalSec - 1) <= now).ToList();
        if (due.Count == 0) return;

        var probes = await Task.WhenAll(due.Select(async c =>
        {
            await _parallel.WaitAsync(ct);
            try { return (check: c, result: await pinger.ProbeAsync(c.Device.Address, c.Port, c.TimeoutMs, ct)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return (check: c, result: new GatewayProbeResult(false, null, ex.GetType().Name)); }
            finally { _parallel.Release(); }
        }));

        var checkIds = due.Select(c => c.Id).ToList();
        var openIncidents = await db.Incidents
            .Where(i => i.Kind == IncidentKind.GatewayDown && i.ClosedUtc == null && i.CheckId != null && checkIds.Contains(i.CheckId.Value))
            .ToDictionaryAsync(i => i.CheckId!.Value, ct);
        var maintenanceSites = await db.MaintenanceWindows
            .Where(m => m.StartUtc <= now && m.EndUtc >= now && m.DeviceId == null && m.SiteId != null)
            .Select(m => m.SiteId!.Value).ToListAsync(ct);

        foreach (var (check, result) in probes)
        {
            if (result.Reachable is null)
            {
                if (now - _lastUnavailableLog > TimeSpan.FromMinutes(10))
                {
                    _lastUnavailableLog = now;
                    log.LogWarning("Gateway probe cannot run in this environment ({Detail}); gateway checks stay Unknown. Set a TCP port on the check or grant CAP_NET_RAW.", result.Detail);
                }
                continue;
            }
            var ok = result.Reachable.Value;
            db.CheckResults.Add(new CheckResult { Time = now, CheckId = check.Id, AgentId = GatewayCheck.AzureProbeId, Ok = ok, LatencyMs = result.LatencyMs, Detail = result.Detail });

            var t = CheckStateMachine.Apply(check.State, check.ConsecutiveFailures, check.FailureThreshold, ok);
            check.State = t.Current; check.ConsecutiveFailures = t.ConsecutiveFailures;
            check.LastResultUtc = now; check.LastLatencyMs = result.LatencyMs;
            if (!t.Changed) continue;
            check.StateChangedUtc = now;

            if (t.WentDown && !openIncidents.ContainsKey(check.Id))
            {
                var suppressed = maintenanceSites.Contains(check.Device.SiteId);
                var inc = new Incident
                {
                    Id = Guid.NewGuid(), Kind = IncidentKind.GatewayDown, SiteId = check.Device.SiteId, CheckId = check.Id, OpenedUtc = now,
                    Summary = $"Gateway {check.Device.Address} at {check.Device.Site.Name} is DOWN from Azure",
                    Notified = suppressed, RecoveryNotified = suppressed,
                };
                db.Incidents.Add(inc); openIncidents[check.Id] = inc;
                log.LogWarning("Gateway {Address} at site {Site} unreachable from Azure ({Detail})", check.Device.Address, check.Device.Site.Name, result.Detail);
            }
            else if (t.Recovered && openIncidents.TryGetValue(check.Id, out var open))
            {
                open.ClosedUtc = now; openIncidents.Remove(check.Id);
                log.LogInformation("Gateway {Address} at site {Site} reachable again from Azure", check.Device.Address, check.Device.Site.Name);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
