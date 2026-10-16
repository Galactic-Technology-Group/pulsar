using System.Collections.Concurrent;
using Nms.Shared.Contracts;

namespace Nms.Api.Services;

/// <summary>In-memory holder for discovery runs (request → result). Results expire after 15 minutes.</summary>
public sealed class DiscoveryStore
{
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset started, DiscoveryResult? result)> _runs = new();

    public void Start(Guid requestId) => _runs[requestId] = (DateTimeOffset.UtcNow, null);

    public void Complete(DiscoveryResult result)
    {
        _runs[result.RequestId] = (DateTimeOffset.UtcNow, result);
        Prune();
    }

    public (bool known, DiscoveryResult? result) Get(Guid requestId) =>
        _runs.TryGetValue(requestId, out var r) ? (true, r.result) : (false, null);

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-15);
        foreach (var (k, v) in _runs) if (v.started < cutoff) _runs.TryRemove(k, out _);
    }
}
