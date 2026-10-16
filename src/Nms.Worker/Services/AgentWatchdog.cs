using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;

namespace Nms.Worker.Services;

/// <summary>
/// Marks agents Offline when their heartbeat is stale and opens one AgentOffline incident per agent
/// (the "site unreachable" alert). Closes it when the agent is seen again.
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

        foreach (var a in agents)
        {
            var stale = a.LastSeenUtc is null || now - a.LastSeenUtc.Value > TimeSpan.FromSeconds(a.HeartbeatIntervalSec * n);
            if (stale && a.Status == AgentStatus.Online)
            {
                a.Status = AgentStatus.Offline;
                if (!openOffline.ContainsKey(a.Id))
                {
                    db.Incidents.Add(new Incident
                    {
                        Id = Guid.NewGuid(), Kind = IncidentKind.AgentOffline, SiteId = a.SiteId, AgentId = a.Id,
                        OpenedUtc = a.LastSeenUtc ?? now, Summary = $"Agent {a.Name} is OFFLINE (site may be unreachable)",
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
