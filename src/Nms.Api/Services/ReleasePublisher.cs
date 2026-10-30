using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nms.Api.Hubs;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared.Contracts;
using Nms.Shared.Hubs;

namespace Nms.Api.Services;

/// <summary>
/// Records an agent release and, when it is the current one, tells every connected agent. Agents that are offline
/// now learn about it from their next heartbeat (see <see cref="AgentHub.Heartbeat"/>).
/// </summary>
public sealed class ReleasePublisher(NmsDbContext db, IHubContext<AgentHub, IAgentClient> hub, ILogger<ReleasePublisher> log)
{
    public async Task<AgentRelease> PublishAsync(ReleaseInput input, CancellationToken ct)
    {
        if (input.IsCurrent)
            await db.AgentReleases.Where(r => r.IsCurrent).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsCurrent, false), ct);

        // Re-publishing a version (e.g. re-imported from GitHub) updates it in place instead of duplicating the row.
        var r = await db.AgentReleases.FirstOrDefaultAsync(x => x.Version == input.Version, ct);
        if (r is null) { r = new AgentRelease { Id = Guid.NewGuid(), Version = input.Version }; db.AgentReleases.Add(r); }
        r.DownloadUrlX64 = input.DownloadUrlX64; r.DownloadUrlArm64 = input.DownloadUrlArm64;
        r.Sha256X64 = input.Sha256X64; r.Sha256Arm64 = input.Sha256Arm64;
        r.IsCurrent = input.IsCurrent; r.PublishedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (r.IsCurrent)
        {
            await hub.Clients.All.UpdateAvailable(ToVersionInfo(r));
            log.LogInformation("Agent release {Version} published as current; connected agents notified", r.Version);
        }
        return r;
    }

    public static AgentVersionInfo ToVersionInfo(AgentRelease r) => new(r.Version, r.DownloadUrlX64, r.DownloadUrlArm64, r.Sha256X64, r.Sha256Arm64);

    /// <summary>The update an agent reporting <paramref name="agentVersion"/> should install, or null when it is already current.</summary>
    public static AgentVersionInfo? PendingUpdate(AgentRelease? current, string? agentVersion) =>
        current is null || string.IsNullOrWhiteSpace(current.Version) || string.Equals(current.Version, agentVersion, StringComparison.OrdinalIgnoreCase)
            ? null : ToVersionInfo(current);
}
