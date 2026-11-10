using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Nms.Data;
using Nms.Data.Entities;

namespace Nms.Api.Services;

/// <summary>
/// Records every human write (the Operator and Admin route groups) in <c>nms.audit_events</c>: who, what route, which
/// object, at which site, and the outcome. Attached to the route groups, so endpoints added later are audited
/// without extra code. Request bodies are never stored: channel settings carry webhook URLs and other secrets.
/// The target's name and site are looked up before the endpoint runs, so a delete still records what was deleted.
/// An audit failure is logged and never fails the request.
/// </summary>
public sealed class AuditFilter(IServiceScopeFactory scopes, ILogger<AuditFilter> log) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var pattern = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value ?? "";
        var targetType = TargetType(pattern);
        var targetId = RouteGuid(http, "id") ?? RouteGuid(http, "deviceId") ?? RouteGuid(http, "siteId");
        var targetTypeForId = RouteGuid(http, "id") is not null ? targetType
            : RouteGuid(http, "deviceId") is not null ? "devices"
            : RouteGuid(http, "siteId") is not null ? "sites" : targetType;

        (Guid? siteId, string? name) before = (RouteGuid(http, "siteId"), null);
        try { before = await DescribeAsync(targetTypeForId, targetId, before.siteId, http.RequestAborted); }
        catch (Exception ex) { log.LogWarning(ex, "Audit lookup failed for {Pattern}", pattern); }

        var status = StatusCodes.Status500InternalServerError;
        object? result = null;
        try
        {
            result = await next(ctx);
            status = StatusOf(result);
            return result;
        }
        finally
        {
            // A create answers 201 with a Location; audit the new object rather than its parent.
            var (createdType, createdId) = result is IResult r && LocationOf(r) is { } loc ? ParseLocation(loc) : (null, null);
            var ev = new AuditEvent
            {
                TimeUtc = DateTimeOffset.UtcNow,
                Actor = ActorName(http.User),
                ActorId = http.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier") ?? http.User.FindFirstValue("oid"),
                Action = $"{http.Request.Method} {pattern}",
                TargetType = createdType ?? targetTypeForId,
                TargetId = createdId ?? targetId,
                SiteId = before.siteId,
                Status = status,
                Detail = before.name,
            };
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
                db.AuditEvents.Add(ev);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex) { log.LogError(ex, "Audit write failed for {Action} by {Actor}", ev.Action, ev.Actor); }
        }
    }

    internal static string ActorName(ClaimsPrincipal user) =>
        user.Identity?.Name ?? user.FindFirstValue("preferred_username") ?? user.FindFirstValue("name") ?? "unknown";

    /// <summary>"/api/devices/{id:guid}/checks" → "devices": the first segment after /api names the object type.</summary>
    internal static string? TargetType(string pattern)
    {
        var parts = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.IndexOf(parts, "api");
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : parts.FirstOrDefault();
    }

    internal static int StatusOf(object? result) => result switch
    {
        IStatusCodeHttpResult { StatusCode: { } code } => code,
        null => StatusCodes.Status204NoContent,
        _ => StatusCodes.Status200OK,   // a plain object is serialized as 200
    };

    /// <summary>Created and Created&lt;T&gt; (and CreatedAtRoute) all expose Location; other results have none.</summary>
    private static string? LocationOf(IResult r) =>
        r is IStatusCodeHttpResult { StatusCode: StatusCodes.Status201Created } ? r.GetType().GetProperty("Location")?.GetValue(r) as string : null;

    /// <summary>"/api/devices/3f…" → ("devices", 3f…).</summary>
    internal static (string? type, Guid? id) ParseLocation(string location)
    {
        var parts = location.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && Guid.TryParse(parts[^1], out var id)) return (parts[^2], id);
        return (null, null);
    }

    private static Guid? RouteGuid(HttpContext http, string key) =>
        http.Request.RouteValues.TryGetValue(key, out var v) && Guid.TryParse(v?.ToString(), out var g) ? g : null;

    private async Task<(Guid? siteId, string? name)> DescribeAsync(string? type, Guid? id, Guid? siteFromRoute, CancellationToken ct)
    {
        if (id is not { } key) return (siteFromRoute, null);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
        switch (type)
        {
            case "sites":
                return (key, await db.Sites.Where(s => s.Id == key).Select(s => "site " + s.Name).FirstOrDefaultAsync(ct));
            case "devices":
                var d = await db.Devices.Where(x => x.Id == key).Select(x => new { x.SiteId, x.Name, x.Address }).FirstOrDefaultAsync(ct);
                return (d?.SiteId ?? siteFromRoute, d is null ? null : $"device {d.Name} ({d.Address})");
            case "checks":
                var c = await db.Checks.Where(x => x.Id == key).Select(x => new { x.Device.SiteId, x.Device.Name, x.Type, x.Port }).FirstOrDefaultAsync(ct);
                return (c?.SiteId, c is null ? null : $"check {c.Type}{(c.Port is null ? "" : ":" + c.Port)} on {c.Name}");
            case "agents":
                var a = await db.Agents.Where(x => x.Id == key).Select(x => new { x.SiteId, x.Name }).FirstOrDefaultAsync(ct);
                return (a?.SiteId, a is null ? null : $"agent {a.Name}");
            case "incidents":
                var i = await db.Incidents.Where(x => x.Id == key).Select(x => new { x.SiteId, x.Summary }).FirstOrDefaultAsync(ct);
                return (i?.SiteId, i is null ? null : $"incident {i.Summary}");
            case "maintenance":
                var m = await db.MaintenanceWindows.Where(x => x.Id == key).Select(x => new { x.SiteId, x.Reason }).FirstOrDefaultAsync(ct);
                return (m?.SiteId, m is null ? null : $"maintenance {m.Reason}");
            case "channels":
                return (null, await db.NotificationChannels.Where(x => x.Id == key).Select(x => "channel " + x.Name).FirstOrDefaultAsync(ct));
            case "claims":
                return (null, await db.AgentClaims.Where(x => x.Id == key).Select(x => "claim " + x.Code + " from " + x.Hostname).FirstOrDefaultAsync(ct));
            default:
                return (siteFromRoute, null);
        }
    }
}
