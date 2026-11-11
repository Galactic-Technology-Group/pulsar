using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nms.Api.Hubs;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;
using Nms.Shared.Contracts;
using Nms.Shared.Hubs;
using Nms.Shared.Snmp;

namespace Nms.Api.Services;

/// <summary>Builds the check list for an agent (all enabled checks at its site) and pushes it on change.</summary>
public sealed class AgentConfigBuilder(NmsDbContext db, IHubContext<AgentHub, IAgentClient> agents)
{
    /// <summary>First agent version with an SNMP probe. Older agents cannot parse CheckType.Snmp, so they never see those checks.</summary>
    public static readonly Version SnmpSince = new(0, 1, 8);

    public async Task<AgentConfigDto> BuildAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await db.Agents.AsNoTracking().Include(a => a.Site).FirstAsync(a => a.Id == agentId, ct);
        var snmp = Supports(agent.Version, SnmpSince);
        // GatewayPing is run by the worker from Azure; agents never see it (and could not parse the enum value).
        var checks = await db.Checks.AsNoTracking().Include(c => c.Device).Include(c => c.SnmpCredential)
            .Where(c => c.Device.SiteId == agent.SiteId && c.Enabled && c.Type != CheckType.GatewayPing && (snmp || c.Type != CheckType.Snmp))
            .ToListAsync(ct);

        var defs = checks.Select(ToDefinition).Where(d => d is not null).Select(d => d!).ToList();
        return new AgentConfigDto(agent.Id, agent.SiteId, agent.Site.VlanMode, agent.HeartbeatIntervalSec, 10, defs, agent.ConfigVersion);
    }

    /// <summary>Null for an SNMP check whose template or credential is gone: nothing for the agent to run.</summary>
    internal static CheckDefinition? ToDefinition(Check c)
    {
        SnmpCredentialDto? cred = null;
        IReadOnlyList<SnmpMetricDef>? metrics = null;
        if (c.Type == CheckType.Snmp)
        {
            if (SnmpTemplates.Find(c.SnmpTemplate) is not { } template || c.SnmpCredential is not { } sc) return null;
            cred = new SnmpCredentialDto(sc.Version, sc.Community, sc.User, sc.Auth, sc.AuthPassword, sc.Priv, sc.PrivPassword, sc.Context);
            metrics = template.Metrics.Select(m => m.ToDef()).ToList();
        }
        return new CheckDefinition(c.Id, c.DeviceId, c.Device.Name, c.Device.Address, c.Device.VlanId, c.Type,
            c.Port, c.HttpPath, c.ExpectedHttpStatus, c.BodyRegex, c.IntervalSec, c.TimeoutMs, c.Enabled, cred, metrics);
    }

    /// <summary>"0.1.8", "0.1.8-beta", "v0.2.0" against a minimum; unknown versions are treated as old.</summary>
    public static bool Supports(string? agentVersion, Version minimum)
    {
        if (string.IsNullOrWhiteSpace(agentVersion)) return false;
        var core = new string(agentVersion.TrimStart('v', 'V').TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
        return Version.TryParse(core, out var v) && v >= minimum;
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

    /// <summary>A credential changed: every site with a check using it gets fresh config.</summary>
    public async Task NotifyCredentialChangedAsync(Guid credentialId, CancellationToken ct = default)
    {
        var sites = await db.Checks.Where(c => c.SnmpCredentialId == credentialId).Select(c => c.Device.SiteId).Distinct().ToListAsync(ct);
        foreach (var s in sites) await NotifySiteChangedAsync(s, ct);
    }
}
