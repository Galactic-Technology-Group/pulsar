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

public sealed record HeartbeatDto(string AgentVersion, DateTimeOffset AgentTimeUtc, IReadOnlyList<InterfaceInfo> Interfaces, int QueuedResults, UpdateRollbackDto? LastRollback = null);

/// <summary>The agent restored its previous binary after a self-update failed. Reported in every heartbeat until a later update confirms itself.</summary>
public sealed record UpdateRollbackDto(string FailedVersion, string? RestoredVersion, string Reason, DateTimeOffset AtUtc);

/// <summary>A single probe outcome. Time is the agent's clock in UTC. Metrics carries SNMP readings (null for other probes).</summary>
public sealed record CheckResultDto(Guid CheckId, DateTimeOffset Time, bool Ok, int? LatencyMs, string? Detail, IReadOnlyList<MetricSampleDto>? Metrics = null);

/// <summary>One reading from an SNMP check. Instance names the table row (interface name, sensor name); null for scalars.</summary>
public sealed record MetricSampleDto(string Key, string? Instance, double Value);

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
    bool Enabled,
    SnmpCredentialDto? Snmp = null,
    IReadOnlyList<SnmpMetricDef>? Metrics = null);

/// <summary>How the agent authenticates to a device's SNMP agent. Sent only to agents, over the hub, never to the portal.</summary>
public sealed record SnmpCredentialDto(SnmpVersion Version, string? Community, string? User = null, SnmpAuth Auth = SnmpAuth.None,
    string? AuthPassword = null, SnmpPriv Priv = SnmpPriv.None, string? PrivPassword = null, string? Context = null);

/// <summary>
/// One value to read. A scalar Oid includes its instance (".0"); a Table Oid is a column that is walked, each row becoming an
/// instance named from LabelOid (the same table's name column) or, without one, the row index. FallbackOid is walked when
/// Oid has no rows (32-bit ifInOctets on a device without the 64-bit ifHCInOctets).
/// </summary>
public sealed record SnmpMetricDef(string Key, string Oid, MetricKind Kind = MetricKind.Gauge, double Scale = 1, bool Table = false, string? LabelOid = null, string? FallbackOid = null);

public sealed record DiscoveryRequest(Guid RequestId, string Cidr, int? VlanId, int TimeoutMs = 1000);

/// <summary>One host found by a sweep. Hostname comes from reverse DNS, mDNS or NetBIOS (HostnameSource says which); null when nothing answered.</summary>
public sealed record DiscoveredHost(string Address, string? MacAddress, int? LatencyMs, string? Hostname = null, string? HostnameSource = null);

public sealed record DiscoveryResult(Guid RequestId, Guid AgentId, int? VlanId, IReadOnlyList<DiscoveredHost> Hosts, string? Error);

public sealed record AgentVersionInfo(string Version, string DownloadUrlX64, string DownloadUrlArm64, string Sha256X64, string Sha256Arm64);
