using Microsoft.EntityFrameworkCore;
using Nms.Api.Services;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;
using Nms.Shared.Contracts;
using Nms.Shared.Snmp;

namespace Nms.Api.Endpoints;

/// <summary>SNMP templates, credentials, per-check thresholds and reading history.</summary>
public static class SnmpEndpoints
{
    public static void Map(RouteGroupBuilder api, RouteGroupBuilder write, RouteGroupBuilder admin)
    {
        api.MapGet("/snmp/templates", () => SnmpTemplates.All);

        // Operators pick credentials when adding checks, so they may list them; only admins see them change. Secrets never leave.
        write.MapGet("/snmp/credentials", async (Guid? siteId, NmsDbContext db, CancellationToken ct) =>
        {
            var q = db.SnmpCredentials.AsNoTracking();
            if (siteId is not null) q = q.Where(c => c.SiteId == null || c.SiteId == siteId);
            var creds = await q.OrderBy(c => c.Name).ToListAsync(ct);
            var used = await db.Checks.Where(c => c.SnmpCredentialId != null).GroupBy(c => c.SnmpCredentialId!.Value)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            return creds.Select(c => View(c, used.GetValueOrDefault(c.Id))).ToList();
        });
        admin.MapPost("/snmp/credentials", async (SnmpCredentialInput input, NmsDbContext db) =>
        {
            if (CredentialError(input, existing: null) is { } error) return Results.BadRequest(new { error });
            if (input.SiteId is { } s && !await db.Sites.AnyAsync(x => x.Id == s)) return Results.NotFound();
            if (await db.SnmpCredentials.AnyAsync(c => c.SiteId == input.SiteId && c.Name == input.Name.Trim()))
                return Results.Conflict(new { error = $"A credential named {input.Name.Trim()} already exists there." });
            var c = new SnmpCredential { Id = Guid.NewGuid() };
            Apply(c, input);
            db.SnmpCredentials.Add(c); await db.SaveChangesAsync();
            return Results.Created($"/api/snmp/credentials/{c.Id}", View(c, 0));
        });
        admin.MapPut("/snmp/credentials/{id:guid}", async (Guid id, SnmpCredentialInput input, NmsDbContext db, AgentConfigBuilder cfg) =>
        {
            var c = await db.SnmpCredentials.FindAsync(id); if (c is null) return Results.NotFound();
            if (CredentialError(input, c) is { } error) return Results.BadRequest(new { error });
            Apply(c, input); await db.SaveChangesAsync(); await cfg.NotifyCredentialChangedAsync(id);
            return Results.Ok(View(c, await db.Checks.CountAsync(x => x.SnmpCredentialId == id)));
        });
        admin.MapDelete("/snmp/credentials/{id:guid}", async (Guid id, NmsDbContext db) =>
        {
            var c = await db.SnmpCredentials.FindAsync(id); if (c is null) return Results.NotFound();
            var used = await db.Checks.CountAsync(x => x.SnmpCredentialId == id);
            if (used > 0) return Results.Conflict(new { error = $"{used} SNMP check(s) use this credential; move or delete them first." });
            db.SnmpCredentials.Remove(c); await db.SaveChangesAsync();
            return Results.NoContent();
        });

        api.MapGet("/checks/{id:guid}/thresholds", (Guid id, NmsDbContext db) =>
            db.MetricThresholds.AsNoTracking().Where(t => t.CheckId == id).OrderBy(t => t.MetricKey).ThenBy(t => t.Label).ToListAsync());
        write.MapPost("/checks/{id:guid}/thresholds", async (Guid id, ThresholdInput input, NmsDbContext db) =>
        {
            var check = await db.Checks.FindAsync(id); if (check is null) return Results.NotFound();
            if (ThresholdError(check, input) is { } error) return Results.BadRequest(new { error });
            var t = new MetricThreshold { Id = Guid.NewGuid(), CheckId = id };
            Apply(t, input);
            db.MetricThresholds.Add(t); await db.SaveChangesAsync();
            return Results.Created($"/api/thresholds/{t.Id}", t);
        });
        write.MapPut("/thresholds/{id:guid}", async (Guid id, ThresholdInput input, NmsDbContext db) =>
        {
            var t = await db.MetricThresholds.Include(x => x.Check).FirstOrDefaultAsync(x => x.Id == id); if (t is null) return Results.NotFound();
            if (ThresholdError(t.Check, input) is { } error) return Results.BadRequest(new { error });
            Apply(t, input);
            // A changed limit is judged afresh on the next poll; an open alert under the old limit is closed quietly.
            await CloseQuietlyAsync(db, x => x.ThresholdId == id);
            await db.SaveChangesAsync();
            return Results.Ok(t);
        });
        write.MapDelete("/thresholds/{id:guid}", async (Guid id, NmsDbContext db) =>
        {
            var t = await db.MetricThresholds.FindAsync(id); if (t is null) return Results.NotFound();
            await CloseQuietlyAsync(db, x => x.ThresholdId == id);
            db.MetricThresholds.Remove(t); await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Raw readings for short ranges, min/avg/max buckets for longer ones (a week at 1/min is 10k rows per series).
        api.MapGet("/checks/{id:guid}/metrics", async (Guid id, string metric, string? instance, DateTimeOffset? from, DateTimeOffset? to, NmsDbContext db, CancellationToken ct) =>
        {
            var f = from ?? DateTimeOffset.UtcNow.AddHours(-24);
            var t = to ?? DateTimeOffset.UtcNow;
            var inst = instance ?? "";
            var rows = await db.MetricSamples.AsNoTracking()
                .Where(s => s.CheckId == id && s.Metric == metric && s.Instance == inst && s.Time >= f && s.Time <= t)
                .OrderBy(s => s.Time).Select(s => new { s.Time, s.Value }).ToListAsync(ct);
            var bucket = Bucket(t - f);
            if (bucket is null) return rows.Select(r => new MetricPoint(r.Time, r.Value)).ToList();
            var ticks = bucket.Value.Ticks;
            return rows.GroupBy(r => new DateTimeOffset(r.Time.UtcTicks / ticks * ticks, TimeSpan.Zero))
                .Select(g => new MetricPoint(g.Key, g.Average(r => r.Value), g.Min(r => r.Value), g.Max(r => r.Value))).ToList();
        });
    }

    internal static TimeSpan? Bucket(TimeSpan range) =>
        range <= TimeSpan.FromHours(6) ? null : range <= TimeSpan.FromDays(2) ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(30);

    internal static SnmpCredentialView View(SnmpCredential c, int checks) =>
        new(c.Id, c.Name, c.Version, c.SiteId, c.User, c.Auth, c.Priv, c.Context,
            HasSecret: c.Version == SnmpVersion.V2c ? !string.IsNullOrEmpty(c.Community) : !string.IsNullOrEmpty(c.User), checks);

    /// <summary>Null when fine. Blank secrets on an update keep the stored ones, so they only count as missing on create.</summary>
    internal static string? CredentialError(SnmpCredentialInput i, SnmpCredential? existing)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return "Give the credential a name.";
        if (i.Version == SnmpVersion.V2c)
            return string.IsNullOrEmpty(i.Community) && string.IsNullOrEmpty(existing?.Community) ? "SNMPv2c needs a community string." : null;
        if (string.IsNullOrWhiteSpace(i.User)) return "SNMPv3 needs a user name.";
        if (i.Priv != SnmpPriv.None && i.Auth == SnmpAuth.None) return "SNMPv3 privacy (encryption) needs an authentication protocol too.";
        // RFC 3414 keys are derived from passwords of at least 8 characters; shorter ones fail on the device anyway.
        if (i.Auth != SnmpAuth.None && (i.AuthPassword?.Length ?? 0) is > 0 and < 8) return "The authentication password must be at least 8 characters.";
        if (i.Priv != SnmpPriv.None && (i.PrivPassword?.Length ?? 0) is > 0 and < 8) return "The privacy password must be at least 8 characters.";
        if (i.Auth != SnmpAuth.None && string.IsNullOrEmpty(i.AuthPassword) && (existing is null || existing.Auth == SnmpAuth.None || string.IsNullOrEmpty(existing.AuthPassword)))
            return "Enter the authentication password.";
        if (i.Priv != SnmpPriv.None && string.IsNullOrEmpty(i.PrivPassword) && (existing is null || existing.Priv == SnmpPriv.None || string.IsNullOrEmpty(existing.PrivPassword)))
            return "Enter the privacy password.";
        return null;
    }

    private static void Apply(SnmpCredential c, SnmpCredentialInput i)
    {
        c.Name = i.Name.Trim(); c.Version = i.Version; c.SiteId = i.SiteId;
        c.Context = string.IsNullOrWhiteSpace(i.Context) ? null : i.Context.Trim();
        if (i.Version == SnmpVersion.V2c)
        {
            if (!string.IsNullOrEmpty(i.Community)) c.Community = i.Community;
            c.User = null; c.Auth = SnmpAuth.None; c.AuthPassword = null; c.Priv = SnmpPriv.None; c.PrivPassword = null;
            return;
        }
        c.Community = null; c.User = i.User!.Trim(); c.Auth = i.Auth; c.Priv = i.Priv;
        if (i.Auth == SnmpAuth.None) c.AuthPassword = null; else if (!string.IsNullOrEmpty(i.AuthPassword)) c.AuthPassword = i.AuthPassword;
        if (i.Priv == SnmpPriv.None) c.PrivPassword = null; else if (!string.IsNullOrEmpty(i.PrivPassword)) c.PrivPassword = i.PrivPassword;
    }

    internal static string? ThresholdError(Check check, ThresholdInput i)
    {
        if (check.Type != CheckType.Snmp) return "Thresholds apply to SNMP checks only.";
        if (SnmpTemplates.Find(check.SnmpTemplate)?.Metric(i.MetricKey) is null) return $"The check's template has no reading called {i.MetricKey}.";
        if (string.IsNullOrWhiteSpace(i.Label)) return "Give the threshold a label; it is the alert's headline.";
        if (!double.IsFinite(i.Value)) return "The limit must be a number.";
        return null;
    }

    private static void Apply(MetricThreshold t, ThresholdInput i)
    {
        t.MetricKey = i.MetricKey; t.Op = i.Op; t.Value = i.Value; t.Label = i.Label.Trim(); t.Enabled = i.Enabled;
        t.Instance = string.IsNullOrWhiteSpace(i.Instance) ? null : i.Instance.Trim();
    }

    /// <summary>The template's default limits, copied onto a new SNMP check.</summary>
    internal static IEnumerable<MetricThreshold> Defaults(Check check) =>
        SnmpTemplates.Find(check.SnmpTemplate)?.Thresholds.Select(d => new MetricThreshold
        {
            Id = Guid.NewGuid(), CheckId = check.Id, MetricKey = d.Metric, Op = d.Op, Value = d.Value, Label = d.Label,
        }) ?? [];

    /// <summary>
    /// Validates the SNMP part of a check: a known template and a credential usable at the device's site. Null when fine
    /// (and for non-SNMP checks).
    /// </summary>
    internal static async Task<string?> CheckError(NmsDbContext db, Device device, CheckInput i)
    {
        if (i.Type != CheckType.Snmp) return null;
        if (SnmpTemplates.Find(i.SnmpTemplate) is null) return "Choose an SNMP template.";
        if (i.SnmpCredentialId is not { } cid) return "Choose an SNMP credential.";
        var cred = await db.SnmpCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid);
        if (cred is null) return "That SNMP credential no longer exists.";
        if (cred.SiteId is { } s && s != device.SiteId) return $"Credential {cred.Name} belongs to another site.";
        return null;
    }

    /// <summary>Closes open incidents without a recovery message: the limit or check they were about is gone or changed.</summary>
    internal static async Task CloseQuietlyAsync(NmsDbContext db, System.Linq.Expressions.Expression<Func<Incident, bool>> which)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var i in await db.Incidents.Where(i => i.ClosedUtc == null).Where(which).ToListAsync())
        {
            i.ClosedUtc = now; i.Notified = true; i.RecoveryNotified = true;
        }
    }
}
