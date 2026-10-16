using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;

namespace Nms.Worker.Services;

/// <summary>
/// Decides which channels an incident goes to, honouring scope, CriticalOnly and per-rule cooldown.
/// Singleton: the cooldown memory must outlive a single polling pass.
/// </summary>
public sealed class AlertRouter
{
    private readonly ConcurrentDictionary<(Guid rule, Guid scopeKey), DateTimeOffset> _lastSent = new();

    public sealed record Target(AlertRule Rule, NotificationChannel Channel);

    public async Task<IReadOnlyList<Target>> ResolveAsync(NmsDbContext db, Incident incident, Device? device, CancellationToken ct)
    {
        var rules = await db.AlertRules.AsNoTracking().Include(r => r.Channel).Where(r => r.Enabled && r.Channel.Enabled).ToListAsync(ct);
        return Resolve(rules, incident, device, DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<Target> Resolve(IEnumerable<AlertRule> rules, Incident incident, Device? device, DateTimeOffset now)
    {
        var targets = new List<Target>();
        foreach (var r in rules)
        {
            if (!Matches(r, incident, device)) continue;
            if (r.CriticalOnly && device is { IsCritical: false }) continue;

            var key = (r.Id, incident.CheckId ?? incident.AgentId ?? incident.SiteId);
            if (_lastSent.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(r.CooldownSec)) continue;
            _lastSent[key] = now;
            targets.Add(new Target(r, r.Channel));
        }
        return targets.DistinctBy(t => t.Channel.Id).ToList();
    }

    public static bool Matches(AlertRule r, Incident i, Device? device) => r.Scope switch
    {
        AlertScope.Global => true,
        AlertScope.Site => r.SiteId == i.SiteId,
        AlertScope.Device => device is not null && r.DeviceId == device.Id,
        AlertScope.Tag => device is not null && r.Tag is not null && device.Tags.Contains(r.Tag, StringComparer.OrdinalIgnoreCase),
        _ => false,
    };
}
