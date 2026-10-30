using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nms.Api.Auth;
using Nms.Api.Services;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;
using Nms.Shared.Contracts;
using Nms.Shared.Hubs;

namespace Nms.Api.Hubs;

[Authorize(Policy = AgentAuth.Policy)]
public sealed class AgentHub(NmsDbContext db, AgentRegistry registry, AgentConfigBuilder configBuilder, IHubContext<PortalHub, IPortalClient> portal, DiscoveryStore discoveries, ILogger<AgentHub> log)
    : Hub<IAgentClient>, IAgentHubServer
{
    public override async Task OnConnectedAsync()
    {
        var agentId = Context.User!.AgentId();
        registry.Add(agentId, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, AgentRegistry.Group(agentId));

        var agent = await db.Agents.FirstAsync(a => a.Id == agentId);
        var was = agent.Status;
        agent.Status = AgentStatus.Online;
        agent.LastSeenUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        if (was != AgentStatus.Online)
            await portal.Clients.Group(PortalHub.SiteGroup(agent.SiteId)).AgentStatusChanged(agent.SiteId, agentId, AgentStatus.Online, agent.LastSeenUtc.Value);
        log.LogInformation("Agent {AgentId} connected", agentId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var agentId = Context.User!.AgentId();
        registry.Remove(agentId, Context.ConnectionId);
        // Status flips to Offline by the worker's heartbeat watchdog, not here: a reconnect to another
        // API replica must not race a disconnect from this one.
        log.LogInformation("Agent {AgentId} disconnected ({Reason})", agentId, exception?.Message ?? "closed");
        await base.OnDisconnectedAsync(exception);
    }

    public async Task Heartbeat(HeartbeatDto hb)
    {
        var agentId = Context.User!.AgentId();
        var agent = await db.Agents.Include(a => a.Interfaces).FirstAsync(a => a.Id == agentId);
        agent.LastSeenUtc = DateTimeOffset.UtcNow;
        agent.Version = hb.AgentVersion;
        if (agent.Status != AgentStatus.Online)
        {
            agent.Status = AgentStatus.Online;
            await portal.Clients.Group(PortalHub.SiteGroup(agent.SiteId)).AgentStatusChanged(agent.SiteId, agentId, AgentStatus.Online, agent.LastSeenUtc.Value);
        }
        UpsertInterfaces(db, agent, hb.Interfaces);
        await db.SaveChangesAsync();
    }

    public Task DiscoveryCompleted(DiscoveryResult result)
    {
        discoveries.Complete(result);
        return Task.CompletedTask;
    }

    public Task<AgentConfigDto> GetConfig() => configBuilder.BuildAsync(Context.User!.AgentId(), Context.ConnectionAborted);

    /// <summary>
    /// New interfaces are added through the context, not the navigation collection: with a pre-set Guid key,
    /// DetectChanges would track them as Modified and the UPDATE would hit zero rows (DbUpdateConcurrencyException).
    /// </summary>
    internal static void UpsertInterfaces(NmsDbContext db, Agent agent, IReadOnlyList<InterfaceInfo> reported)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var i in reported)
        {
            var existing = agent.Interfaces.FirstOrDefault(x => x.Name == i.Name && x.Address == i.Address);
            if (existing is null)
                db.AgentInterfaces.Add(new AgentInterface { Id = Guid.NewGuid(), AgentId = agent.Id, Name = i.Name, VlanId = i.VlanId, Address = i.Address, PrefixLength = i.PrefixLength, LastReportedUtc = now });
            else { existing.VlanId = i.VlanId; existing.PrefixLength = i.PrefixLength; existing.LastReportedUtc = now; }
        }
    }
}
