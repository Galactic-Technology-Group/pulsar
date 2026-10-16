using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nms.Data;

namespace Nms.Worker.Services;

/// <summary>Daily cleanup. Timescale's retention policy drops raw results; this prunes old closed incidents and expired maintenance windows.</summary>
public sealed class RetentionJob(IServiceScopeFactory scopes, IOptions<WorkerOptions> options, ILogger<RetentionJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
                var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.IncidentRetentionDays);
                var incidents = await db.Incidents.Where(i => i.ClosedUtc != null && i.ClosedUtc < cutoff).ExecuteDeleteAsync(ct);
                var windows = await db.MaintenanceWindows.Where(m => m.EndUtc < DateTimeOffset.UtcNow.AddDays(-30)).ExecuteDeleteAsync(ct);
                // Without Timescale (local dev) the raw table would grow forever.
                var results = await db.CheckResults.Where(r => r.Time < DateTimeOffset.UtcNow.AddDays(-options.Value.RetentionDays)).ExecuteDeleteAsync(ct);
                log.LogInformation("Retention: removed {Incidents} incidents, {Windows} maintenance windows, {Results} raw results", incidents, windows, results);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Retention pass failed"); }
            await Task.Delay(TimeSpan.FromHours(24), ct);
        }
    }
}
