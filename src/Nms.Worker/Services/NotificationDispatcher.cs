using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;
using Nms.Worker.Notifications;

namespace Nms.Worker.Services;

/// <summary>
/// Polls for incidents that still need a notification (open and Notified == false, or closed and RecoveryNotified == false),
/// waits a short digest window so a burst becomes one message per channel, then sends and marks them.
/// </summary>
public sealed class NotificationDispatcher(
    IServiceScopeFactory scopes,
    IEnumerable<INotificationSender> senders,
    AlertRouter router,
    IOptions<WorkerOptions> options,
    ILogger<NotificationDispatcher> log) : BackgroundService
{
    private readonly Dictionary<NotificationChannelType, INotificationSender> _senders = senders.ToDictionary(s => s.Type);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Notification pass failed"); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.NotifyPollSec), ct);
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
        var digestCutoff = DateTimeOffset.UtcNow.AddSeconds(-options.Value.DigestWindowSec);

        var pendingDown = await db.Incidents
            .Where(i => !i.Notified && i.ClosedUtc == null && i.OpenedUtc <= digestCutoff).OrderBy(i => i.OpenedUtc).Take(500).ToListAsync(ct);
        var pendingRecovery = await db.Incidents
            .Where(i => i.Notified && !i.RecoveryNotified && i.ClosedUtc != null).OrderBy(i => i.ClosedUtc).Take(500).ToListAsync(ct);
        // Incidents that opened and closed inside the digest window never bother anyone.
        var flaps = await db.Incidents.Where(i => !i.Notified && i.ClosedUtc != null).ToListAsync(ct);
        foreach (var f in flaps) { f.Notified = true; f.RecoveryNotified = true; }

        if (pendingDown.Count == 0 && pendingRecovery.Count == 0) { await db.SaveChangesAsync(ct); return; }

        var sites = await db.Sites.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var checkIds = pendingDown.Concat(pendingRecovery).Where(i => i.CheckId != null).Select(i => i.CheckId!.Value).Distinct().ToList();
        var devices = await db.Checks.AsNoTracking().Where(c => checkIds.Contains(c.Id)).Select(c => new { c.Id, c.Device }).ToDictionaryAsync(x => x.Id, x => x.Device, ct);

        await DispatchGroupAsync(pendingDown, isRecovery: false);
        await DispatchGroupAsync(pendingRecovery, isRecovery: true);
        await db.SaveChangesAsync(ct);

        async Task DispatchGroupAsync(List<Incident> incidents, bool isRecovery)
        {
            var perChannel = new Dictionary<Guid, (NotificationChannel channel, List<NotificationLine> lines)>();
            foreach (var i in incidents)
            {
                var device = i.CheckId is { } cid && devices.TryGetValue(cid, out var d) ? d : null;
                var targets = await router.ResolveAsync(db, i, device, ct);
                var siteName = sites.GetValueOrDefault(i.SiteId, "unknown site");
                var text = isRecovery ? i.Summary.Replace("is DOWN", "recovered").Replace("is OFFLINE", "is back online") : i.Summary;
                foreach (var t in targets)
                {
                    if (!perChannel.TryGetValue(t.Channel.Id, out var entry)) perChannel[t.Channel.Id] = entry = (t.Channel, new List<NotificationLine>());
                    entry.lines.Add(new NotificationLine(siteName, text, isRecovery ? i.ClosedUtc!.Value : i.OpenedUtc, i.Kind));
                }
                if (isRecovery) i.RecoveryNotified = true; else i.Notified = true;
            }

            foreach (var (channel, lines) in perChannel.Values)
            {
                if (!_senders.TryGetValue(channel.Type, out var sender)) { log.LogWarning("No sender for channel type {Type}", channel.Type); continue; }
                var title = lines.Count == 1
                    ? (isRecovery ? "RECOVERED: " : "DOWN: ") + lines[0].Text
                    : $"{lines.Count} {(isRecovery ? "recoveries" : "outages")} across {lines.Select(l => l.Site).Distinct().Count()} site(s)";
                var msg = new NotificationMessage(title, isRecovery ? "The following checks recovered." : "The following checks failed their threshold.", lines, isRecovery,
                    string.IsNullOrEmpty(options.Value.PortalBaseUrl) ? null : options.Value.PortalBaseUrl.TrimEnd('/') + "/incidents");
                try { await sender.SendAsync(channel, msg, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Send to {Channel} failed; incidents marked notified to avoid a retry storm", channel.Name); }
            }
        }
    }
}
