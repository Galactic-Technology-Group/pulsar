using Microsoft.EntityFrameworkCore;
using Nms.Data.Entities;
using Nms.Shared;

namespace Nms.Data;

/// <summary>
/// Every site gets one built-in "Internet line" device whose single Internet check pings a few public anycast
/// addresses from the agent. It answers the question an agent-offline alert cannot: was the site's WAN down,
/// or only the path between agent and service? The results are buffered on the agent like any other check, so
/// they arrive after the outage and the worker uses them to annotate the AgentOffline incident.
/// </summary>
public static class InternetLine
{
    public const string DeviceName = "Internet line";
    public const string Tag = "internet";
    /// <summary>Cloudflare, Google and Quad9 public resolvers: three independent networks, all anycast.</summary>
    public const string DefaultTargets = "1.1.1.1, 8.8.8.8, 9.9.9.9";
    public const int IntervalSec = 30;
    public const int TimeoutMs = 2000;
    public const int FailureThreshold = 3;

    public static Device Create(Guid siteId) => new()
    {
        Id = Guid.NewGuid(), SiteId = siteId, Kind = DeviceKind.InternetLine, Name = DeviceName, Address = DefaultTargets,
        IsCritical = true, Tags = [Tag],
        Checks = { new Check { Id = Guid.NewGuid(), Type = CheckType.Internet, IntervalSec = IntervalSec, TimeoutMs = TimeoutMs, FailureThreshold = FailureThreshold } },
    };

    /// <summary>Adds the built-in device to sites that predate it. Idempotent; run at API start.</summary>
    public static async Task<int> EnsureForAllSitesAsync(NmsDbContext db, CancellationToken ct = default)
    {
        var missing = await db.Sites.Where(s => !s.Devices.Any(d => d.Kind == DeviceKind.InternetLine)).Select(s => s.Id).ToListAsync(ct);
        foreach (var siteId in missing) db.Devices.Add(Create(siteId));
        if (missing.Count > 0) await db.SaveChangesAsync(ct);
        return missing.Count;
    }

    /// <summary>Splits the Address field ("1.1.1.1, 8.8.8.8") into individual targets.</summary>
    public static string[] ParseTargets(string address) =>
        address.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
