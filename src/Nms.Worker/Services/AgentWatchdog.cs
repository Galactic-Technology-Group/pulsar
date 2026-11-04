using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;

namespace Nms.Worker.Services;

/// <summary>
/// Marks agents Offline when their heartbeat is stale and opens one AgentOffline incident per agent
/// (the "site unreachable" alert). Closes it when the agent is seen again. The incident summary carries the
/// live cross-check: whether the site's gateway is answering from Azure at the moment the agent went quiet.
/// </summary>
public sealed class AgentWatchdog(IServiceScopeFactory scopes, IOptions<WorkerOptions> options, ILogger<AgentWatchdog> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Watchdog pass failed"); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.WatchdogPollSec), ct);
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var n = options.Value.OfflineAfterHeartbeats;

        var agents = await db.Agents.Where(a => a.Status == AgentStatus.Online || a.Status == AgentStatus.Offline).ToListAsync(ct);
        var openOffline = await db.Incidents.Where(i => i.Kind == IncidentKind.AgentOffline && i.ClosedUtc == null).ToDictionaryAsync(i => i.AgentId!.Value, ct);
        var gateways = await db.Checks.AsNoTracking()
            .Where(c => c.Type == CheckType.GatewayPing && c.Enabled && c.Device.Kind == DeviceKind.Gateway && c.Device.Address != "")
            .Select(c => new { c.Device.SiteId, c.Device.Address, c.State })
            .ToListAsync(ct);

        foreach (var a in agents)
        {
            var stale = a.LastSeenUtc is null || now - a.LastSeenUtc.Value > TimeSpan.FromSeconds(a.HeartbeatIntervalSec * n);
            if (stale && a.Status == AgentStatus.Online)
            {
                a.Status = AgentStatus.Offline;
                if (!openOffline.ContainsKey(a.Id))
                {
                    var gw = gateways.FirstOrDefault(g => g.SiteId == a.SiteId);
                    var summary = gw?.State switch
                    {
                        CheckState.Down => $"Agent {a.Name} is OFFLINE; gateway {gw.Address} is also unreachable from Azure: site WAN is down",
                        CheckState.Up => $"Agent {a.Name} is OFFLINE but gateway {gw.Address} answers from Azure: agent host or LAN problem",
                        _ => $"Agent {a.Name} is OFFLINE (site may be unreachable)",
                    };
                    db.Incidents.Add(new Incident
                    {
                        Id = Guid.NewGuid(), Kind = IncidentKind.AgentOffline, SiteId = a.SiteId, AgentId = a.Id,
                        OpenedUtc = a.LastSeenUtc ?? now, Summary = summary,
                    });
                }
                log.LogWarning("Agent {Agent} marked offline (last seen {LastSeen})", a.Name, a.LastSeenUtc);
            }
            else if (!stale && openOffline.TryGetValue(a.Id, out var inc))
            {
                inc.ClosedUtc = now;
                log.LogInformation("Agent {Agent} back online", a.Name);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
