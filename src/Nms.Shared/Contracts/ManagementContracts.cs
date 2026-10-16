using Nms.Shared;

namespace Nms.Shared.Contracts;

// Request/response shapes shared by the API and the portal. Kept flat and explicit on purpose.
public sealed record SiteInput(string Name, string TimeZone = "UTC", VlanMode VlanMode = VlanMode.Routed);
public sealed record AgentInput(string Name, int HeartbeatIntervalSec = 30);
public sealed record AgentCreated(Guid AgentId, string EnrollmentToken, DateTimeOffset ExpiresUtc);
public sealed record DeviceInput(string Name, string Address, int? VlanId, bool IsCritical = true, string[]? Tags = null, string? Notes = null);
public sealed record CheckInput(CheckType Type, int? Port = null, string? HttpPath = null, int? ExpectedHttpStatus = null, string? BodyRegex = null,
    int IntervalSec = 60, int TimeoutMs = 2000, int FailureThreshold = 3, bool Enabled = true);
public sealed record ChannelInput(string Name, NotificationChannelType Type, string ConfigJson, bool Enabled = true);
public sealed record RuleInput(string Name, AlertScope Scope, Guid ChannelId, Guid? SiteId = null, Guid? DeviceId = null, string? Tag = null, bool CriticalOnly = true, int CooldownSec = 300, bool Enabled = true);
public sealed record MaintenanceInput(Guid? SiteId, Guid? DeviceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);
public sealed record ReleaseInput(string Version, string DownloadUrlX64, string DownloadUrlArm64, string Sha256X64, string Sha256Arm64, bool IsCurrent = true);
public sealed record SiteSummary(Guid Id, string Name, int Devices, int Up, int Down, int Degraded, int Unknown, int AgentsOnline, int AgentsTotal, int OpenIncidents);
public sealed record LatencyPoint(DateTimeOffset Bucket, long Samples, long OkSamples, double? AvgLatencyMs, int? MaxLatencyMs);

