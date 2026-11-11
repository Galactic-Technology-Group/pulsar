using Nms.Shared;

namespace Nms.Data.Entities;

public class Site
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string TimeZone { get; set; } = "UTC";
    public VlanMode VlanMode { get; set; } = VlanMode.Routed;
    /// <summary>Street address, free text. Feeds the geographic site map; sourced from Field Service later.</summary>
    public string? Address { get; set; }
    /// <summary>WGS 84 coordinates for the map pin; both set or both null.</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<Agent> Agents { get; set; } = new List<Agent>();
    public ICollection<Device> Devices { get; set; } = new List<Device>();
}

public class Agent
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;
    public string Name { get; set; } = "";
    public string? Hostname { get; set; }
    /// <summary>SHA-256 of the one-time enrollment token; cleared after use.</summary>
    public string? EnrollmentTokenHash { get; set; }
    public DateTimeOffset? EnrollmentTokenExpiresUtc { get; set; }
    /// <summary>PBKDF2 hash of the agent secret issued at enrollment.</summary>
    public string? SecretHash { get; set; }
    public string? Version { get; set; }
    /// <summary>Set while the agent reports that it rolled back a self-update from this version; cleared when a later update confirms.</summary>
    public string? RollbackFromVersion { get; set; }
    public string? RollbackReason { get; set; }
    public DateTimeOffset? RollbackUtc { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public AgentStatus Status { get; set; } = AgentStatus.PendingEnrollment;
    public int HeartbeatIntervalSec { get; set; } = 30;
    /// <summary>Monotonic config version; bumped whenever checks for this agent change.</summary>
    public long ConfigVersion { get; set; } = 1;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<AgentInterface> Interfaces { get; set; } = new List<AgentInterface>();
}

public class AgentInterface
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Agent Agent { get; set; } = null!;
    public string Name { get; set; } = "";
    public int? VlanId { get; set; }
    public string Address { get; set; } = "";
    public int PrefixLength { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset LastReportedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class Device
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    /// <summary>
    /// Hardware identity (canonical lowercase aa:bb:cc:dd:ee:ff, unique per site). Survives DHCP churn: discovery
    /// matches on it first and offers to move the device when its address changed.
    /// </summary>
    public string? MacAddress { get; set; }
    public int? VlanId { get; set; }
    public bool IsCritical { get; set; } = true;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string? Notes { get; set; }
    /// <summary>Gateway devices are created with the site; their Address is the site's public IP or hostname, probed from Azure.</summary>
    public DeviceKind Kind { get; set; } = DeviceKind.Host;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<Check> Checks { get; set; } = new List<Check>();
}

public class Check
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public CheckType Type { get; set; }
    public int? Port { get; set; }
    public string? HttpPath { get; set; }
    public int? ExpectedHttpStatus { get; set; }
    public string? BodyRegex { get; set; }
    public int IntervalSec { get; set; } = 60;
    public int TimeoutMs { get; set; } = 2000;
    /// <summary>Consecutive failures before the check is considered Down.</summary>
    public int FailureThreshold { get; set; } = 3;
    public bool Enabled { get; set; } = true;
    // Current evaluated state (denormalised for dashboards)
    public CheckState State { get; set; } = CheckState.Unknown;
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? LastResultUtc { get; set; }
    public int? LastLatencyMs { get; set; }
    public DateTimeOffset? StateChangedUtc { get; set; }
    /// <summary>SNMP checks: key into the template library (Nms.Shared/Snmp/templates.json).</summary>
    public string? SnmpTemplate { get; set; }
    public Guid? SnmpCredentialId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public SnmpCredential? SnmpCredential { get; set; }
    /// <summary>SNMP checks: the newest readings as JSON (MetricSampleDto[]), denormalised for the device page.</summary>
    public string? LastMetricsJson { get; set; }
    public ICollection<MetricThreshold> Thresholds { get; set; } = new List<MetricThreshold>();
}

/// <summary>
/// How agents log in to devices' SNMP agents. SiteId null makes it usable at every site. Community and passwords are
/// write-only through the API (never returned) and reach only the agents of sites whose checks use them.
/// </summary>
public class SnmpCredential
{
    public Guid Id { get; set; }
    public Guid? SiteId { get; set; }
    public string Name { get; set; } = "";
    public SnmpVersion Version { get; set; }
    public string? Community { get; set; }
    public string? User { get; set; }
    public SnmpAuth Auth { get; set; }
    public string? AuthPassword { get; set; }
    public SnmpPriv Priv { get; set; }
    public string? PrivPassword { get; set; }
    public string? Context { get; set; }
}

/// <summary>A limit on one SNMP reading. Instance null applies to every row of a table metric.</summary>
public class MetricThreshold
{
    public Guid Id { get; set; }
    public Guid CheckId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Check Check { get; set; } = null!;
    public string MetricKey { get; set; } = "";
    public string? Instance { get; set; }
    public ThresholdOp Op { get; set; }
    public double Value { get; set; }
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>Timescale hypertable of SNMP readings. Instance is "" for scalars so it can sit in the key.</summary>
public class MetricSample
{
    public DateTimeOffset Time { get; set; }
    public Guid CheckId { get; set; }
    public string Metric { get; set; } = "";
    public string Instance { get; set; } = "";
    public double Value { get; set; }
}

/// <summary>Timescale hypertable. No single-column PK; (Time, CheckId) is unique.</summary>
public class CheckResult
{
    public DateTimeOffset Time { get; set; }
    public Guid CheckId { get; set; }
    public Guid AgentId { get; set; }
    public bool Ok { get; set; }
    public int? LatencyMs { get; set; }
    public string? Detail { get; set; }
}

public class Incident
{
    public Guid Id { get; set; }
    public IncidentKind Kind { get; set; }
    public Guid SiteId { get; set; }
    public Guid? CheckId { get; set; }
    public Guid? AgentId { get; set; }
    public DateTimeOffset OpenedUtc { get; set; }
    public DateTimeOffset? ClosedUtc { get; set; }
    public string? AckBy { get; set; }
    public DateTimeOffset? AckUtc { get; set; }
    public string Summary { get; set; } = "";
    /// <summary>Threshold incidents: which limit, and which table row (interface, probe) crossed it ("" for scalars).</summary>
    public Guid? ThresholdId { get; set; }
    public string? MetricInstance { get; set; }
    public bool Notified { get; set; }
    public bool RecoveryNotified { get; set; }
}

public class NotificationChannel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public NotificationChannelType Type { get; set; }
    /// <summary>JSON. Email: {"to":["a@b"]}. Teams: {"secretName":"teams-webhook-x"} (Key Vault ref) or {"url":"..."}.</summary>
    public string ConfigJson { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
}

public class AlertRule
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public AlertScope Scope { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? DeviceId { get; set; }
    public string? Tag { get; set; }
    public Guid ChannelId { get; set; }
    public NotificationChannel Channel { get; set; } = null!;
    public bool CriticalOnly { get; set; } = true;
    public int CooldownSec { get; set; } = 300;
    public bool Enabled { get; set; } = true;
}

public class MaintenanceWindow
{
    public Guid Id { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? DeviceId { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>
/// A claim-code enrollment in progress. Rows are short-lived: pruned a day after expiry. No secret is ever stored
/// here; the agent's secret is generated at collection time and written to <see cref="Agent.SecretHash"/> only.
/// </summary>
public class AgentClaim
{
    public Guid Id { get; set; }
    /// <summary>Normalized 8-character code (no dash), unique while pending.</summary>
    public string Code { get; set; } = "";
    /// <summary>SHA-256 of the random key the agent generated; proves the poller is the box that asked.</summary>
    public string ClaimKeyHash { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string? AgentVersion { get; set; }
    public string? RequestedName { get; set; }
    /// <summary>JSON array of InterfaceInfo as reported at claim time, shown to the approver.</summary>
    public string InterfacesJson { get; set; } = "[]";
    public string? RemoteAddress { get; set; }
    public ClaimState State { get; set; } = ClaimState.Pending;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresUtc { get; set; }
    public Guid? AgentId { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedUtc { get; set; }
}

public class AgentRelease
{
    public Guid Id { get; set; }
    public string Version { get; set; } = "";
    public string DownloadUrlX64 { get; set; } = "";
    public string DownloadUrlArm64 { get; set; } = "";
    public string Sha256X64 { get; set; } = "";
    public string Sha256Arm64 { get; set; } = "";
    public bool IsCurrent { get; set; }
    public DateTimeOffset PublishedUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Filled by the releases endpoint: agents currently reporting a rollback from this version. Not stored.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public int RolledBackAgents { get; set; }
}

/// <summary>
/// One human write through the management API: who did what to which object, at which site, and the HTTP outcome.
/// Kept indefinitely as client-facing audit evidence; never stores request bodies.
/// </summary>
public class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset TimeUtc { get; set; }
    public string Actor { get; set; } = "";
    /// <summary>Entra object id of the user, when present.</summary>
    public string? ActorId { get; set; }
    /// <summary>HTTP method and route template, e.g. "DELETE /api/devices/{id:guid}".</summary>
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public Guid? SiteId { get; set; }
    public int Status { get; set; }
    /// <summary>Human-readable target, captured before the change ("device core-sw (10.0.0.2)").</summary>
    public string? Detail { get; set; }
}
