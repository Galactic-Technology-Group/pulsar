using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nms.Api.Hubs;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;
using Nms.Shared.Contracts;
using Nms.Shared.Hubs;
using Nms.Shared.Snmp;

namespace Nms.Api.Services;

/// <summary>
/// Persists a batch of results, advances each check's state machine, and opens/closes incidents. SNMP readings are stored
/// in metric_samples and the newest ones are compared with the check's thresholds.
/// Notifications are sent by the worker, which polls incidents with Notified == false.
/// </summary>
public sealed class ResultIngestService(NmsDbContext db, IHubContext<PortalHub, IPortalClient> portal, ILogger<ResultIngestService> log)
{
    public async Task<ResultsAck> IngestAsync(Guid agentId, Guid siteId, IReadOnlyList<CheckResultDto> results, CancellationToken ct)
    {
        if (results.Count == 0) return new ResultsAck(0, 0);

        var checkIds = results.Select(r => r.CheckId).Distinct().ToList();
        var checks = await db.Checks.Include(c => c.Device).ThenInclude(d => d.Site)
            .Where(c => checkIds.Contains(c.Id) && c.Device.SiteId == siteId && c.Type != CheckType.GatewayPing)   // gateway samples come from the worker only
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
        var newestReadings = new Dictionary<Guid, (CheckResultDto result, Check check)>();

        foreach (var r in results.OrderBy(r => r.Time))
        {
            if (!checks.TryGetValue(r.CheckId, out var check)) continue;      // unknown or foreign check: drop silently
            if (!seen.Add((r.CheckId, r.Time))) { duplicates++; continue; }
            // Reject clock skew beyond 1 hour in the future.
            if (r.Time > now.AddHours(1)) { duplicates++; continue; }

            db.CheckResults.Add(new CheckResult { CheckId = r.CheckId, AgentId = agentId, Time = r.Time, Ok = r.Ok, LatencyMs = r.LatencyMs, Detail = Truncate(r.Detail) });
            accepted++;
            if (r.Metrics is { Count: > 0 } && check.Type == CheckType.Snmp) AddSamples(r);

            // Only the newest result per check drives state (replays of old history must not flap it).
            if (check.LastResultUtc is not null && r.Time < check.LastResultUtc) continue;

            var t = CheckStateMachine.Apply(check.State, check.ConsecutiveFailures, check.FailureThreshold, r.Ok);
            check.State = t.Current;
            check.ConsecutiveFailures = t.ConsecutiveFailures;
            check.LastResultUtc = r.Time;
            check.LastLatencyMs = r.LatencyMs;
            if (r.Ok && r.Metrics is not null && check.Type == CheckType.Snmp) newestReadings[check.Id] = (r, check);
            if (t.Changed) { check.StateChangedUtc = r.Time; events.Add((check, t, r.Time)); }
        }

        foreach (var (check, t, at) in events)
        {
            var inMaintenance = maintenance.Any(m => m.DeviceId == check.DeviceId || (m.DeviceId == null && m.SiteId == siteId));
            if (t.WentDown && !openIncidents.ContainsKey(check.Id))
            {
                var suppressed = agentOffline || inMaintenance || !check.Device.IsCritical;
                var inc = new Incident
                {
                    Id = Guid.NewGuid(), Kind = IncidentKind.CheckDown, SiteId = siteId, CheckId = check.Id, AgentId = agentId,
                    OpenedUtc = at, Summary = $"{check.Device.Name} ({check.Device.Address}) {check.Type}{(check.Port is null ? "" : ":" + check.Port)} is DOWN",
                    // Suppressed alerts are recorded but marked already-notified (down and recovery) so the worker skips them.
                    Notified = suppressed, RecoveryNotified = suppressed,
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

        if (newestReadings.Count > 0) await EvaluateThresholdsAsync(newestReadings.Values, siteId, agentId, agentOffline, maintenance, ct);

        await db.SaveChangesAsync(ct);

        foreach (var (check, t, at) in events)
            await portal.Clients.Groups(PortalHub.SiteGroup(siteId), PortalHub.AllGroup).CheckStateChanged(siteId, check.DeviceId, check.Id, t.Current, at);

        log.LogDebug("Agent {AgentId}: accepted {Accepted}, duplicates {Duplicates}, transitions {Transitions}", agentId, accepted, duplicates, events.Count);
        return new ResultsAck(accepted, duplicates);
    }

    private static string? Truncate(string? s) => s is { Length: > 512 } ? s[..512] : s;

    private void AddSamples(CheckResultDto r)
    {
        var keys = new HashSet<(string, string)>();
        foreach (var m in r.Metrics!)
        {
            var instance = Truncate128(m.Instance ?? "");
            if (m.Key.Length > 64 || !double.IsFinite(m.Value) || !keys.Add((m.Key, instance))) continue;
            db.MetricSamples.Add(new MetricSample { Time = r.Time, CheckId = r.CheckId, Metric = m.Key, Instance = instance, Value = m.Value });
        }
    }

    private static string Truncate128(string s) => s.Length > 128 ? s[..128] : s;

    /// <summary>
    /// Opens a Threshold incident for every (limit, row) newly breached and closes it once the reading is back inside the
    /// limit. Rows missing from a poll leave their incident as it is. Suppression follows check-down incidents: recorded,
    /// but not paged, while the agent is offline, during maintenance, or for non-critical devices.
    /// </summary>
    private async Task EvaluateThresholdsAsync(IEnumerable<(CheckResultDto result, Check check)> readings, Guid siteId, Guid agentId,
        bool agentOffline, List<MaintenanceWindow> maintenance, CancellationToken ct)
    {
        var list = readings.ToList();
        var ids = list.Select(x => x.check.Id).ToList();
        var thresholds = await db.MetricThresholds.Where(t => ids.Contains(t.CheckId) && t.Enabled).ToListAsync(ct);
        var open = await db.Incidents
            .Where(i => i.Kind == IncidentKind.Threshold && i.ClosedUtc == null && i.CheckId != null && ids.Contains(i.CheckId.Value))
            .ToListAsync(ct);
        var openByKey = open.Where(i => i.ThresholdId != null).ToDictionary(i => (i.ThresholdId!.Value, i.MetricInstance ?? ""));

        foreach (var (r, check) in list)
        {
            check.LastMetricsJson = JsonSerializer.Serialize(r.Metrics, NmsJson.Options);
            var template = SnmpTemplates.Find(check.SnmpTemplate);
            var inMaintenance = maintenance.Any(m => m.DeviceId == check.DeviceId || (m.DeviceId == null && m.SiteId == siteId));
            foreach (var e in Evaluate(thresholds.Where(t => t.CheckId == check.Id), r.Metrics!))
            {
                var key = (e.Threshold.Id, e.Instance);
                if (e.Breached && !openByKey.ContainsKey(key))
                {
                    var suppressed = agentOffline || inMaintenance || !check.Device.IsCritical;
                    var inc = new Incident
                    {
                        Id = Guid.NewGuid(), Kind = IncidentKind.Threshold, SiteId = siteId, CheckId = check.Id, AgentId = agentId,
                        ThresholdId = e.Threshold.Id, MetricInstance = e.Instance, OpenedUtc = r.Time,
                        Summary = ThresholdSummary(check.Device, e.Threshold, template?.Metric(e.Threshold.MetricKey), e.Instance, e.Value),
                        Notified = suppressed, RecoveryNotified = suppressed,
                    };
                    db.Incidents.Add(inc);
                    openByKey[key] = inc;
                }
                else if (!e.Breached && openByKey.Remove(key, out var inc)) inc.ClosedUtc = r.Time;
            }
        }
    }

    internal readonly record struct ThresholdEval(MetricThreshold Threshold, string Instance, double Value, bool Breached);

    /// <summary>Every (limit, reading) pair that applies: a limit with no instance covers every row of its metric.</summary>
    internal static IEnumerable<ThresholdEval> Evaluate(IEnumerable<MetricThreshold> thresholds, IReadOnlyList<MetricSampleDto> samples)
    {
        foreach (var t in thresholds)
            foreach (var s in samples)
                if (s.Key == t.MetricKey && (t.Instance is null || t.Instance == (s.Instance ?? "")))
                    yield return new ThresholdEval(t, s.Instance ?? "", s.Value, SnmpTemplates.Breached(t.Op, s.Value, t.Value));
    }

    /// <summary>"ups-1 (10.0.0.9): UPS on battery (Power source is battery)".</summary>
    internal static string ThresholdSummary(Device device, MetricThreshold t, SnmpTemplateMetric? metric, string instance, double value)
    {
        var reading = metric?.Format(value) ?? value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var name = (metric?.Name ?? t.MetricKey) + (instance.Length == 0 ? "" : $" [{instance}]");
        return $"{device.Name} ({device.Address}): {t.Label} ({name} is {reading})";
    }
}
