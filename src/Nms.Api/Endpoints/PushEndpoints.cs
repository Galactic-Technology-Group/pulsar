using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared.Contracts;

namespace Nms.Api.Endpoints;

/// <summary>
/// Registry of phones that receive push notifications. Any signed-in user may register or remove their own phone
/// (it is not configuration, so it is not audited); administrators can list and remove any.
/// </summary>
public static partial class PushEndpoints
{
    public static void Map(RouteGroupBuilder api, RouteGroupBuilder admin)
    {
        api.MapPost("/push/devices", async (PushDeviceInput input, ClaimsPrincipal user, NmsDbContext db, CancellationToken ct) =>
        {
            if (InputError(input) is { } bad) return Results.BadRequest(new { error = bad });
            var token = input.Token.Trim().ToLowerInvariant();
            var device = await db.PushDevices.FirstOrDefaultAsync(d => d.Token == token, ct);
            if (device is null) db.PushDevices.Add(device = new PushDevice { Id = Guid.NewGuid(), Token = token });
            // A token belongs to whoever is signed in on that phone now.
            device.Environment = input.Environment.Trim().ToLowerInvariant();
            device.UserId = user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier") ?? user.FindFirstValue("oid");
            device.UserName = Truncate(user.Identity?.Name ?? user.FindFirstValue("preferred_username") ?? user.FindFirstValue("name") ?? "unknown", 256);
            device.DeviceName = string.IsNullOrWhiteSpace(input.DeviceName) ? null : Truncate(input.DeviceName.Trim(), 128);
            device.LastSeenUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Sign-out on the phone: knowing the token is the proof of ownership.
        api.MapDelete("/push/devices/by-token/{token}", async (string token, NmsDbContext db, CancellationToken ct) =>
        {
            var t = token.Trim().ToLowerInvariant();
            await db.PushDevices.Where(d => d.Token == t).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        admin.MapGet("/push/devices", (NmsDbContext db) =>
            db.PushDevices.AsNoTracking().OrderBy(d => d.UserName).ThenByDescending(d => d.LastSeenUtc)
                .Select(d => new PushDeviceView(d.Id, d.UserName, d.DeviceName, d.Environment, d.CreatedUtc, d.LastSeenUtc)).ToListAsync());

        admin.MapDelete("/push/devices/{id:guid}", async (Guid id, NmsDbContext db) =>
            await db.PushDevices.Where(d => d.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound());
    }

    internal static string? InputError(PushDeviceInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Token) || !HexToken().IsMatch(input.Token.Trim())) return "Token must be the APNs device token as hex.";
        if (input.Environment?.Trim().ToLowerInvariant() is not ("sandbox" or "production")) return "Environment must be sandbox or production.";
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    [GeneratedRegex("^[0-9a-fA-F]{32,200}$")]
    private static partial Regex HexToken();
}
