using Nms.Shared;

namespace Nms.Data.Entities;

public class Site
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string TimeZone { get; set; } = "UTC";
    public VlanMode VlanMode { get; set; } = VlanMode.Routed;
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
    public int? VlanId { get; set; }
    public bool IsCritical { get; set; } = true;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string? Notes { get; set; }
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
}
