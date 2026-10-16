using Nms.Shared.Contracts;

namespace Nms.Shared.Hubs;

public static class HubRoutes
{
    public const string Agent = "/hubs/agent";
    public const string Portal = "/hubs/portal";
}

/// <summary>Methods the server invokes on a connected agent (server -> agent).</summary>
public interface IAgentClient
{
    Task ConfigUpdated(AgentConfigDto config);
    Task RunCheckNow(Guid checkId);
    Task StartDiscovery(DiscoveryRequest request);
    Task UpdateAvailable(AgentVersionInfo version);
    Task Revoked(string reason);
}

/// <summary>Methods an agent invokes on the server (agent -> server). Names must match hub method names.</summary>
public interface IAgentHubServer
{
    Task Heartbeat(HeartbeatDto heartbeat);
    Task DiscoveryCompleted(DiscoveryResult result);
    Task<AgentConfigDto> GetConfig();
}

/// <summary>Live events pushed to portal browsers.</summary>
public interface IPortalClient
{
    Task CheckStateChanged(Guid siteId, Guid deviceId, Guid checkId, CheckState state, DateTimeOffset at);
    Task AgentStatusChanged(Guid siteId, Guid agentId, AgentStatus status, DateTimeOffset at);
    Task IncidentChanged(Guid incidentId, Guid siteId, bool open);
}
