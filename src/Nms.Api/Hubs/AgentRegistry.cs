using System.Collections.Concurrent;

namespace Nms.Api.Hubs;

/// <summary>Tracks which SignalR connection belongs to which agent, on this API instance.</summary>
public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<Guid, string> _connections = new();

    public void Add(Guid agentId, string connectionId) => _connections[agentId] = connectionId;
    public void Remove(Guid agentId, string connectionId) => _connections.TryRemove(new KeyValuePair<Guid, string>(agentId, connectionId));
    public bool IsConnected(Guid agentId) => _connections.ContainsKey(agentId);
    public IReadOnlyCollection<Guid> Connected => _connections.Keys.ToArray();

    public static string Group(Guid agentId) => $"agent:{agentId}";
}
