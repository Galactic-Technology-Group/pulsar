using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nms.Api.Hubs;
using Nms.Data;
using Nms.Shared.Contracts;
using Nms.Shared.Hubs;

namespace Nms.Api.Services;

/// <summary>Builds the check list for an agent (all enabled checks at its site) and pushes it on change.</summary>
public sealed class AgentConfigBuilder(NmsDbContext db, IHubContext<AgentHub, IAgentClient> agents)
{
    public async Task<AgentConfigDto> BuildAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await db.Agents.AsNoTracking().Include(a => a.Site).FirstAsync(a => a.Id == agentId, ct);
        var checks = await db.Checks.AsNoTracking()
            .Where(c => c.Device.SiteId == agent.SiteId && c.Enabled)
            .Select(c => new CheckDefinition(c.Id, c.DeviceId, c.Device.Name, c.Device.Address, c.Device.VlanId, c.Type,
                c.Port, c.HttpPath, c.ExpectedHttpStatus, c.BodyRegex, c.IntervalSec, c.TimeoutMs, c.Enabled))
            .ToListAsync(ct);

        return new AgentConfigDto(agent.Id, agent.SiteId, agent.Site.VlanMode, agent.HeartbeatIntervalSec, 10, checks, agent.ConfigVersion);
    }

    /// <summary>Call after any change to devices/checks/site at a site: bumps config versions and pushes to connected agents.</summary>
    public async Task NotifySiteChangedAsync(Guid siteId, CancellationToken ct = default)
    {
        var siteAgents = await db.Agents.Where(a => a.SiteId == siteId).ToListAsync(ct);
        foreach (var a in siteAgents) a.ConfigVersion++;
        await db.SaveChangesAsync(ct);
        foreach (var a in siteAgents)
        {
            var cfg = await BuildAsync(a.Id, ct);
            await agents.Clients.Group(AgentRegistry.Group(a.Id)).ConfigUpdated(cfg);
        }
    }
}
