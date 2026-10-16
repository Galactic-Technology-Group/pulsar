using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nms.Api.Hubs;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;
using Nms.Shared.Contracts;
using Nms.Shared.Hubs;

namespace Nms.Api.Services;

/// <summary>
/// Persists a batch of results, advances each check's state machine, and opens/closes incidents.
/// Notifications are sent by the worker, which polls incidents with Notified == false.
/// </summary>
public sealed class ResultIngestService(NmsDbContext db, IHubContext<PortalHub, IPortalClient> portal, ILogger<ResultIngestService> log)
{
    public async Task<ResultsAck> IngestAsync(Guid agentId, Guid siteId, IReadOnlyList<CheckResultDto> results, CancellationToken ct)
    {
        if (results.Count == 0) return new ResultsAck(0, 0);

        var checkIds = results.Select(r => r.CheckId).Distinct().ToList();
        var checks = await db.Checks.Include(c => c.Device)
            .Where(c => checkIds.Contains(c.Id) && c.Device.SiteId == siteId)
            .ToDictionaryAsync(c => c.Id, ct);

        // Dedupe against what already exists for these (check, time) pairs. Timescale compressed chunks
        // reject PK conflicts loudly, so check first instead of relying on ON CONFLICT.
        var minTime = results.Min(r => r.Time);
        var maxTime = results.Max(r => r.Time);
        var existing = await db.CheckResults.AsNoTracking()
            .Where(r => checkIds.Contains(r.CheckId) && r.Time >= minTime && r.Time <= maxTime)
            .Select(r => new { r.CheckId, r.Time })
            .ToListAsync(ct);
        var seen = existing.Select(e => (e.CheckId, e.Time)).ToHashSet();

        var now = DateTimeOffset.UtcNow;
        int accepted = 0, duplicates = 0;
        var openIncidents = await db.Incidents
            .Where(i => i.Kind == IncidentKind.CheckDown && i.ClosedUtc == null && i.CheckId != null && checkIds.Contains(i.CheckId.Value))
            .ToDictionaryAsync(i => i.CheckId!.Value, ct);
        var agentOffline = await db.Incidents.AnyAsync(i => i.Kind == IncidentKind.AgentOffline && i.AgentId == agentId && i.ClosedUtc == null, ct);
        var maintenance = await db.MaintenanceWindows
            .Where(m => m.StartUtc <= now && m.EndUtc >= now && (m.SiteId == siteId || m.DeviceId != null))
            .ToListAsync(ct);

        var events = new List<(Check check, StateTransition t, DateTimeOffset at)>();

        foreach (var r in results.OrderBy(r => r.Time))
        {
            if (!checks.TryGetValue(r.CheckId, out var check)) continue;      // unknown or foreign check: drop silently
            if (!seen.Add((r.CheckId, r.Time))) { duplicates++; continue; }
            // Reject clock skew beyond 1 hour in the future.
            if (r.Time > now.AddHours(1)) { duplicates++; continue; }

            db.CheckResults.Add(new CheckResult { CheckId = r.CheckId, AgentId = agentId, Time = r.Time, Ok = r.Ok, LatencyMs = r.LatencyMs, Detail = Truncate(r.Detail) });
            accepted++;

            // Only the newest result per check drives state (replays of old history must not flap it).
            if (check.LastResultUtc is not null && r.Time < check.LastResultUtc) continue;

            var t = CheckStateMachine.Apply(check.State, check.ConsecutiveFailures, check.FailureThreshold, r.Ok);
            check.State = t.Current;
            check.ConsecutiveFailures = t.ConsecutiveFailures;
            check.LastResultUtc = r.Time;
            check.LastLatencyMs = r.LatencyMs;
            if (t.Changed) { check.StateChangedUtc = r.Time; events.Add((check, t, r.Time)); }
        }

        foreach (var (check, t, at) in events)
        {
            var inMaintenance = maintenance.Any(m => m.DeviceId == check.DeviceId || (m.DeviceId == null && m.SiteId == siteId));
            if (t.WentDown && !openIncidents.ContainsKey(check.Id))
            {
                var inc = new Incident
                {
                    Id = Guid.NewGuid(), Kind = IncidentKind.CheckDown, SiteId = siteId, CheckId = check.Id, AgentId = agentId,
                    OpenedUtc = at, Summary = $"{check.Device.Name} ({check.Device.Address}) {check.Type}{(check.Port is null ? "" : ":" + check.Port)} is DOWN",
                    // Suppressed alerts are recorded but marked already-notified so the worker skips them.
                    Notified = agentOffline || inMaintenance || !check.Device.IsCritical,
                };
                db.Incidents.Add(inc);
                openIncidents[check.Id] = inc;
            }
            else if (t.Recovered && openIncidents.TryGetValue(check.Id, out var open))
            {
                open.ClosedUtc = at;
                openIncidents.Remove(check.Id);
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var (check, t, at) in events)
            await portal.Clients.Groups(PortalHub.SiteGroup(siteId), PortalHub.AllGroup).CheckStateChanged(siteId, check.DeviceId, check.Id, t.Current, at);

        log.LogDebug("Agent {AgentId}: accepted {Accepted}, duplicates {Duplicates}, transitions {Transitions}", agentId, accepted, duplicates, events.Count);
        return new ResultsAck(accepted, duplicates);
    }

    private static string? Truncate(string? s) => s is { Length: > 512 } ? s[..512] : s;
}
