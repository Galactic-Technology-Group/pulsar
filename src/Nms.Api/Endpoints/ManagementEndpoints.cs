using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nms.Api.Auth;
using Nms.Api.Hubs;
using Nms.Api.Services;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;
using Nms.Shared.Contracts;
using Nms.Shared.Hubs;

namespace Nms.Api.Endpoints;

public static class ManagementEndpoints
{
    public static IEndpointRouteBuilder MapManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(Policies.User).WithTags("Management");
        var write = api.MapGroup("").RequireAuthorization(p => p.RequireRole(Policies.Admin, Policies.Operator));
        var admin = api.MapGroup("").RequireAuthorization(p => p.RequireRole(Policies.Admin));

        // ---- Dashboard -------------------------------------------------------------------------
        api.MapGet("/dashboard/sites", async (NmsDbContext db, CancellationToken ct) =>
        {
            var sites = await db.Sites.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
            var checkAgg = await db.Checks.AsNoTracking().Where(c => c.Enabled)
                .GroupBy(c => new { c.Device.SiteId, c.State })
                .Select(g => new { g.Key.SiteId, g.Key.State, Count = g.Count() }).ToListAsync(ct);
            var deviceCount = await db.Devices.AsNoTracking().GroupBy(d => d.SiteId).Select(g => new { SiteId = g.Key, Count = g.Count() }).ToListAsync(ct);
            var agents = await db.Agents.AsNoTracking().GroupBy(a => a.SiteId)
                .Select(g => new { SiteId = g.Key, Total = g.Count(), Online = g.Count(a => a.Status == AgentStatus.Online) }).ToListAsync(ct);
            var incidents = await db.Incidents.AsNoTracking().Where(i => i.ClosedUtc == null).GroupBy(i => i.SiteId).Select(g => new { SiteId = g.Key, Count = g.Count() }).ToListAsync(ct);

            int C(Guid s, CheckState st) => checkAgg.FirstOrDefault(x => x.SiteId == s && x.State == st)?.Count ?? 0;
            return sites.Select(s => new SiteSummary(s.Id, s.Name,
                deviceCount.FirstOrDefault(x => x.SiteId == s.Id)?.Count ?? 0,
                C(s.Id, CheckState.Up), C(s.Id, CheckState.Down), C(s.Id, CheckState.Degraded), C(s.Id, CheckState.Unknown),
                agents.FirstOrDefault(x => x.SiteId == s.Id)?.Online ?? 0, agents.FirstOrDefault(x => x.SiteId == s.Id)?.Total ?? 0,
                incidents.FirstOrDefault(x => x.SiteId == s.Id)?.Count ?? 0));
        });

        // ---- Sites -----------------------------------------------------------------------------
        api.MapGet("/sites", (NmsDbContext db) => db.Sites.AsNoTracking().OrderBy(s => s.Name).ToListAsync());
        api.MapGet("/sites/{id:guid}", async (Guid id, NmsDbContext db) =>
            await db.Sites.AsNoTracking().Include(s => s.Agents).ThenInclude(a => a.Interfaces).FirstOrDefaultAsync(s => s.Id == id) is { } s ? Results.Ok(s) : Results.NotFound());
        write.MapPost("/sites", async (SiteInput input, NmsDbContext db) =>
        {
            var s = new Site { Id = Guid.NewGuid(), Name = input.Name.Trim(), TimeZone = input.TimeZone, VlanMode = input.VlanMode };
            db.Sites.Add(s); await db.SaveChangesAsync();
            return Results.Created($"/api/sites/{s.Id}", s);
        });
        write.MapPut("/sites/{id:guid}", async (Guid id, SiteInput input, NmsDbContext db, AgentConfigBuilder cfg) =>
        {
            var s = await db.Sites.FindAsync(id); if (s is null) return Results.NotFound();
            s.Name = input.Name.Trim(); s.TimeZone = input.TimeZone; s.VlanMode = input.VlanMode;
            await db.SaveChangesAsync(); await cfg.NotifySiteChangedAsync(id);
            return Results.Ok(s);
        });
        admin.MapDelete("/sites/{id:guid}", async (Guid id, NmsDbContext db) =>
            await db.Sites.Where(s => s.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound());

        // ---- Agents ----------------------------------------------------------------------------
        api.MapGet("/sites/{siteId:guid}/agents", (Guid siteId, NmsDbContext db, AgentRegistry reg) =>
            db.Agents.AsNoTracking().Include(a => a.Interfaces).Where(a => a.SiteId == siteId).OrderBy(a => a.Name).ToListAsync());
        write.MapPost("/sites/{siteId:guid}/agents", async (Guid siteId, AgentInput input, NmsDbContext db) =>
        {
            if (!await db.Sites.AnyAsync(s => s.Id == siteId)) return Results.NotFound();
            var token = SecretHasher.NewToken(32);
            var a = new Agent
            {
                Id = Guid.NewGuid(), SiteId = siteId, Name = input.Name.Trim(), HeartbeatIntervalSec = Math.Clamp(input.HeartbeatIntervalSec, 10, 300),
                EnrollmentTokenHash = SecretHasher.Sha256(token), EnrollmentTokenExpiresUtc = DateTimeOffset.UtcNow.AddHours(24),
            };
            db.Agents.Add(a); await db.SaveChangesAsync();
            // The plaintext token is shown exactly once.
            return Results.Created($"/api/agents/{a.Id}", new AgentCreated(a.Id, token, a.EnrollmentTokenExpiresUtc.Value));
        });
        write.MapPost("/agents/{id:guid}/reissue-token", async (Guid id, NmsDbContext db) =>
        {
            var a = await db.Agents.FindAsync(id); if (a is null) return Results.NotFound();
            var token = SecretHasher.NewToken(32);
            a.EnrollmentTokenHash = SecretHasher.Sha256(token); a.EnrollmentTokenExpiresUtc = DateTimeOffset.UtcNow.AddHours(24);
            a.SecretHash = null; a.Status = AgentStatus.PendingEnrollment;
            await db.SaveChangesAsync();
            return Results.Ok(new AgentCreated(a.Id, token, a.EnrollmentTokenExpiresUtc.Value));
        });
        admin.MapPost("/agents/{id:guid}/revoke", async (Guid id, NmsDbContext db, IHubContext<AgentHub, IAgentClient> hub) =>
        {
            var a = await db.Agents.FindAsync(id); if (a is null) return Results.NotFound();
            a.Status = AgentStatus.Revoked; a.SecretHash = null; await db.SaveChangesAsync();
            await hub.Clients.Group(AgentRegistry.Group(id)).Revoked("Revoked by administrator");
            return Results.NoContent();
        });
        admin.MapDelete("/agents/{id:guid}", async (Guid id, NmsDbContext db) =>
            await db.Agents.Where(a => a.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound());
        write.MapPost("/agents/{id:guid}/discover", async (Guid id, DiscoveryRequest req, NmsDbContext db, AgentRegistry reg, DiscoveryStore store, IHubContext<AgentHub, IAgentClient> hub) =>
        {
            if (!await db.Agents.AnyAsync(a => a.Id == id)) return Results.NotFound();
            if (!reg.IsConnected(id)) return Results.Conflict("Agent is not connected");
            var r = req with { RequestId = Guid.NewGuid() };
            store.Start(r.RequestId);
            await hub.Clients.Group(AgentRegistry.Group(id)).StartDiscovery(r);
            return Results.Accepted($"/api/discoveries/{r.RequestId}", new { r.RequestId });
        });
        api.MapGet("/discoveries/{requestId:guid}", (Guid requestId, DiscoveryStore store) =>
        {
            var (known, result) = store.Get(requestId);
            return !known ? Results.NotFound() : result is null ? Results.Accepted() : Results.Ok(result);
        });

        // ---- Devices & checks ------------------------------------------------------------------
        api.MapGet("/sites/{siteId:guid}/devices", (Guid siteId, NmsDbContext db) =>
            db.Devices.AsNoTracking().Include(d => d.Checks).Where(d => d.SiteId == siteId).OrderBy(d => d.Name).ToListAsync());
        api.MapGet("/devices/{id:guid}", async (Guid id, NmsDbContext db) =>
            await db.Devices.AsNoTracking().Include(d => d.Checks).FirstOrDefaultAsync(d => d.Id == id) is { } d ? Results.Ok(d) : Results.NotFound());
        write.MapPost("/sites/{siteId:guid}/devices", async (Guid siteId, DeviceInput input, NmsDbContext db, AgentConfigBuilder cfg) =>
        {
            if (!await db.Sites.AnyAsync(s => s.Id == siteId)) return Results.NotFound();
            var d = new Device { Id = Guid.NewGuid(), SiteId = siteId, Name = input.Name.Trim(), Address = input.Address.Trim(), VlanId = input.VlanId, IsCritical = input.IsCritical, Tags = input.Tags ?? [], Notes = input.Notes };
            // Every device gets an ICMP check by default; the portal can add port/HTTP checks afterwards.
            d.Checks.Add(new Check { Id = Guid.NewGuid(), Type = CheckType.Icmp });
            db.Devices.Add(d); await db.SaveChangesAsync(); await cfg.NotifySiteChangedAsync(siteId);
            return Results.Created($"/api/devices/{d.Id}", d);
        });
        write.MapPut("/devices/{id:guid}", async (Guid id, DeviceInput input, NmsDbContext db, AgentConfigBuilder cfg) =>
        {
            var d = await db.Devices.FindAsync(id); if (d is null) return Results.NotFound();
            d.Name = input.Name.Trim(); d.Address = input.Address.Trim(); d.VlanId = input.VlanId; d.IsCritical = input.IsCritical; d.Tags = input.Tags ?? []; d.Notes = input.Notes;
            await db.SaveChangesAsync(); await cfg.NotifySiteChangedAsync(d.SiteId);
            return Results.Ok(d);
        });
        write.MapDelete("/devices/{id:guid}", async (Guid id, NmsDbContext db, AgentConfigBuilder cfg) =>
        {
            var d = await db.Devices.FindAsync(id); if (d is null) return Results.NotFound();
            db.Devices.Remove(d); await db.SaveChangesAsync(); await cfg.NotifySiteChangedAsync(d.SiteId);
            return Results.NoContent();
        });
        write.MapPost("/devices/{deviceId:guid}/checks", async (Guid deviceId, CheckInput input, NmsDbContext db, AgentConfigBuilder cfg) =>
        {
            var d = await db.Devices.FindAsync(deviceId); if (d is null) return Results.NotFound();
            var c = new Check { Id = Guid.NewGuid(), DeviceId = deviceId };
            Apply(c, input);
            db.Checks.Add(c); await db.SaveChangesAsync(); await cfg.NotifySiteChangedAsync(d.SiteId);
            return Results.Created($"/api/checks/{c.Id}", c);
        });
        write.MapPut("/checks/{id:guid}", async (Guid id, CheckInput input, NmsDbContext db, AgentConfigBuilder cfg) =>
        {
            var c = await db.Checks.Include(x => x.Device).FirstOrDefaultAsync(x => x.Id == id); if (c is null) return Results.NotFound();
            Apply(c, input); await db.SaveChangesAsync(); await cfg.NotifySiteChangedAsync(c.Device.SiteId);
            return Results.Ok(c);
        });
        write.MapDelete("/checks/{id:guid}", async (Guid id, NmsDbContext db, AgentConfigBuilder cfg) =>
        {
            var c = await db.Checks.Include(x => x.Device).FirstOrDefaultAsync(x => x.Id == id); if (c is null) return Results.NotFound();
            db.Checks.Remove(c); await db.SaveChangesAsync(); await cfg.NotifySiteChangedAsync(c.Device.SiteId);
            return Results.NoContent();
        });
        write.MapPost("/checks/{id:guid}/run", async (Guid id, NmsDbContext db, IHubContext<AgentHub, IAgentClient> hub) =>
        {
            var siteId = await db.Checks.Where(c => c.Id == id).Select(c => (Guid?)c.Device.SiteId).FirstOrDefaultAsync();
            if (siteId is null) return Results.NotFound();
            foreach (var a in await db.Agents.Where(a => a.SiteId == siteId).Select(a => a.Id).ToListAsync())
                await hub.Clients.Group(AgentRegistry.Group(a)).RunCheckNow(id);
            return Results.Accepted();
        });

        api.MapGet("/checks/{id:guid}/history", async (Guid id, NmsDbContext db, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        {
            var f = from ?? DateTimeOffset.UtcNow.AddHours(-24);
            var t = to ?? DateTimeOffset.UtcNow;
            // Use the 5-minute continuous aggregate when it exists; fall back to raw rows in local dev.
            try
            {
                return Results.Ok(await db.Database.SqlQuery<LatencyPoint>($"""
                    SELECT bucket AS "Bucket", samples AS "Samples", ok_samples AS "OkSamples", avg_latency_ms AS "AvgLatencyMs", max_latency_ms AS "MaxLatencyMs"
                    FROM nms.check_results_5m WHERE check_id = {id} AND bucket >= {f} AND bucket <= {t} ORDER BY bucket
                    """).ToListAsync(ct));
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
            {
                var raw = await db.CheckResults.AsNoTracking().Where(r => r.CheckId == id && r.Time >= f && r.Time <= t).OrderBy(r => r.Time).ToListAsync(ct);
                return Results.Ok(raw.GroupBy(r => new DateTimeOffset(r.Time.UtcTicks / TimeSpan.TicksPerMinute / 5 * 5 * TimeSpan.TicksPerMinute, TimeSpan.Zero))
                    .Select(g => new LatencyPoint(g.Key, g.Count(), g.Count(r => r.Ok), g.Average(r => r.LatencyMs), g.Max(r => r.LatencyMs))).ToList());
            }
        });
        api.MapGet("/checks/{id:guid}/results", (Guid id, NmsDbContext db, int limit = 100) =>
            db.CheckResults.AsNoTracking().Where(r => r.CheckId == id).OrderByDescending(r => r.Time).Take(Math.Clamp(limit, 1, 1000)).ToListAsync());

        // ---- Incidents -------------------------------------------------------------------------
        api.MapGet("/incidents", (NmsDbContext db, bool open = true, Guid? siteId = null, int limit = 200) =>
            db.Incidents.AsNoTracking()
                .Where(i => (!open || i.ClosedUtc == null) && (siteId == null || i.SiteId == siteId))
                .OrderByDescending(i => i.OpenedUtc).Take(Math.Clamp(limit, 1, 1000)).ToListAsync());
        write.MapPost("/incidents/{id:guid}/ack", async (Guid id, ClaimsPrincipal user, NmsDbContext db) =>
        {
            var i = await db.Incidents.FindAsync(id); if (i is null) return Results.NotFound();
            i.AckBy = user.Identity?.Name ?? user.FindFirstValue("preferred_username") ?? "unknown"; i.AckUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(); return Results.Ok(i);
        });

        // ---- Settings --------------------------------------------------------------------------
        admin.MapGet("/channels", (NmsDbContext db) => db.NotificationChannels.AsNoTracking().OrderBy(c => c.Name).ToListAsync());
        admin.MapPost("/channels", async (ChannelInput input, NmsDbContext db) =>
        {
            var c = new NotificationChannel { Id = Guid.NewGuid(), Name = input.Name, Type = input.Type, ConfigJson = input.ConfigJson, Enabled = input.Enabled };
            db.NotificationChannels.Add(c); await db.SaveChangesAsync(); return Results.Created($"/api/channels/{c.Id}", c);
        });
        admin.MapPut("/channels/{id:guid}", async (Guid id, ChannelInput input, NmsDbContext db) =>
        {
            var c = await db.NotificationChannels.FindAsync(id); if (c is null) return Results.NotFound();
            c.Name = input.Name; c.Type = input.Type; c.ConfigJson = input.ConfigJson; c.Enabled = input.Enabled;
            await db.SaveChangesAsync(); return Results.Ok(c);
        });
        admin.MapDelete("/channels/{id:guid}", async (Guid id, NmsDbContext db) =>
            await db.NotificationChannels.Where(c => c.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound());

        admin.MapGet("/rules", (NmsDbContext db) => db.AlertRules.AsNoTracking().Include(r => r.Channel).OrderBy(r => r.Name).ToListAsync());
        admin.MapPost("/rules", async (RuleInput input, NmsDbContext db) =>
        {
            var r = new AlertRule { Id = Guid.NewGuid() }; Apply(r, input);
            db.AlertRules.Add(r); await db.SaveChangesAsync(); return Results.Created($"/api/rules/{r.Id}", r);
        });
        admin.MapPut("/rules/{id:guid}", async (Guid id, RuleInput input, NmsDbContext db) =>
        {
            var r = await db.AlertRules.FindAsync(id); if (r is null) return Results.NotFound();
            Apply(r, input); await db.SaveChangesAsync(); return Results.Ok(r);
        });
        admin.MapDelete("/rules/{id:guid}", async (Guid id, NmsDbContext db) =>
            await db.AlertRules.Where(r => r.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound());

        api.MapGet("/maintenance", (NmsDbContext db) => db.MaintenanceWindows.AsNoTracking().Where(m => m.EndUtc >= DateTimeOffset.UtcNow).OrderBy(m => m.StartUtc).ToListAsync());
        write.MapPost("/maintenance", async (MaintenanceInput input, NmsDbContext db) =>
        {
            if (input.EndUtc <= input.StartUtc) return Results.BadRequest("EndUtc must be after StartUtc");
            var m = new MaintenanceWindow { Id = Guid.NewGuid(), SiteId = input.SiteId, DeviceId = input.DeviceId, StartUtc = input.StartUtc, EndUtc = input.EndUtc, Reason = input.Reason };
            db.MaintenanceWindows.Add(m); await db.SaveChangesAsync(); return Results.Created($"/api/maintenance/{m.Id}", m);
        });
        write.MapDelete("/maintenance/{id:guid}", async (Guid id, NmsDbContext db) =>
            await db.MaintenanceWindows.Where(m => m.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound());

        admin.MapGet("/releases", (NmsDbContext db) => db.AgentReleases.AsNoTracking().OrderByDescending(r => r.PublishedUtc).ToListAsync());
        admin.MapPost("/releases", async (ReleaseInput input, ReleasePublisher publisher, CancellationToken ct) =>
        {
            var r = await publisher.PublishAsync(input, ct);
            return Results.Created($"/api/releases/{r.Id}", r);
        });
        // One click in the portal: read the GitHub release (latest, or a tag), take the CI-built binaries and
        // checksums, publish as current and push to every connected agent.
        admin.MapPost("/releases/import", async (ReleaseImportRequest req, GitHubReleaseSource github, ReleasePublisher publisher, CancellationToken ct) =>
        {
            ReleaseInput input;
            try { input = await github.FetchAsync(req.Tag, ct); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity); }
            var r = await publisher.PublishAsync(input with { IsCurrent = true }, ct);
            return Results.Created($"/api/releases/{r.Id}", r);
        });
        admin.MapGet("/releases/source", (GitHubReleaseSource github) => Results.Ok(new { github.Repo }));

        return app;
    }

    private static void Apply(Check c, CheckInput i)
    {
        c.Type = i.Type; c.Port = i.Port; c.HttpPath = i.HttpPath; c.ExpectedHttpStatus = i.ExpectedHttpStatus; c.BodyRegex = i.BodyRegex;
        c.IntervalSec = Math.Clamp(i.IntervalSec, 5, 3600); c.TimeoutMs = Math.Clamp(i.TimeoutMs, 200, 30000);
        c.FailureThreshold = Math.Clamp(i.FailureThreshold, 1, 20); c.Enabled = i.Enabled;
    }

    private static void Apply(AlertRule r, RuleInput i)
    {
        r.Name = i.Name; r.Scope = i.Scope; r.ChannelId = i.ChannelId; r.SiteId = i.SiteId; r.DeviceId = i.DeviceId; r.Tag = i.Tag;
        r.CriticalOnly = i.CriticalOnly; r.CooldownSec = Math.Max(0, i.CooldownSec); r.Enabled = i.Enabled;
    }
}
