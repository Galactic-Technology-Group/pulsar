using Microsoft.EntityFrameworkCore;
using Nms.Data.Entities;
using Nms.Shared;

namespace Nms.Data;

/// <summary>
/// Every site gets one built-in "Gateway" device: the site's public address, probed by the worker from Azure rather
/// than by the agent. It is the outside-in cross-check for an agent-offline alert: gateway unreachable means the
/// site's WAN is down; gateway answering means the agent host or its LAN is the problem.
/// The address is empty until an operator fills it in; the check stays Unknown until then.
/// </summary>
public static class GatewayCheck
{
    public const string DeviceName = "Gateway";
    public const string Tag = "gateway";
    public const int IntervalSec = 30;
    public const int TimeoutMs = 2000;
    public const int FailureThreshold = 3;
    /// <summary>Results written by the worker carry this in place of an agent id.</summary>
    public static readonly Guid AzureProbeId = Guid.Empty;

    public static Device Create(Guid siteId) => new()
    {
        Id = Guid.NewGuid(), SiteId = siteId, Kind = DeviceKind.Gateway, Name = DeviceName, Address = "",
        IsCritical = true, Tags = [Tag],
        Checks = { new Check { Id = Guid.NewGuid(), Type = CheckType.GatewayPing, IntervalSec = IntervalSec, TimeoutMs = TimeoutMs, FailureThreshold = FailureThreshold } },
    };

    /// <summary>Adds the built-in device to sites that predate it. Idempotent; run at API start.</summary>
    public static async Task<int> EnsureForAllSitesAsync(NmsDbContext db, CancellationToken ct = default)
    {
        var missing = await db.Sites.Where(s => !s.Devices.Any(d => d.Kind == DeviceKind.Gateway)).Select(s => s.Id).ToListAsync(ct);
        foreach (var siteId in missing) db.Devices.Add(Create(siteId));
        if (missing.Count > 0) await db.SaveChangesAsync(ct);
        return missing.Count;
    }
}
