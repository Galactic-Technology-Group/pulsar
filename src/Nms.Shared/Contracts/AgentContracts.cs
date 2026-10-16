namespace Nms.Shared.Contracts;

public static class AgentAuth
{
    /// <summary>Header carrying the agent id (GUID).</summary>
    public const string AgentIdHeader = "X-Nms-Agent-Id";
    /// <summary>Header carrying the per-agent secret issued at enrollment.</summary>
    public const string AgentSecretHeader = "X-Nms-Agent-Secret";
    public const string Scheme = "NmsAgent";
    public const string Policy = "Agent";
}

public sealed record EnrollRequest(string EnrollmentToken, string Hostname, string AgentVersion, IReadOnlyList<InterfaceInfo> Interfaces);

public sealed record EnrollResponse(Guid AgentId, string Secret, Guid SiteId, string SiteName);

public sealed record InterfaceInfo(string Name, int? VlanId, string Address, int PrefixLength);

public sealed record HeartbeatDto(string AgentVersion, DateTimeOffset AgentTimeUtc, IReadOnlyList<InterfaceInfo> Interfaces, int QueuedResults);

/// <summary>A single probe outcome. Time is the agent's clock in UTC.</summary>
public sealed record CheckResultDto(Guid CheckId, DateTimeOffset Time, bool Ok, int? LatencyMs, string? Detail);

public sealed record ResultsBatch(Guid AgentId, IReadOnlyList<CheckResultDto> Results);

public sealed record ResultsAck(int Accepted, int Duplicates);

/// <summary>Everything an agent needs to run its checks. Pushed on connect and on change.</summary>
public sealed record AgentConfigDto(
    Guid AgentId,
    Guid SiteId,
    VlanMode VlanMode,
    int HeartbeatIntervalSec,
    int ResultsFlushIntervalSec,
    IReadOnlyList<CheckDefinition> Checks,
    long Version);

public sealed record CheckDefinition(
    Guid CheckId,
    Guid DeviceId,
    string DeviceName,
    string TargetAddress,
    int? VlanId,
    CheckType Type,
    int? Port,
    string? HttpPath,
    int? ExpectedHttpStatus,
    string? BodyRegex,
    int IntervalSec,
    int TimeoutMs,
    bool Enabled);

public sealed record DiscoveryRequest(Guid RequestId, string Cidr, int? VlanId, int TimeoutMs = 1000);

public sealed record DiscoveredHost(string Address, string? MacAddress, int? LatencyMs);

public sealed record DiscoveryResult(Guid RequestId, Guid AgentId, int? VlanId, IReadOnlyList<DiscoveredHost> Hosts, string? Error);

public sealed record AgentVersionInfo(string Version, string DownloadUrlX64, string DownloadUrlArm64, string Sha256X64, string Sha256Arm64);
