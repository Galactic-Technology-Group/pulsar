using Nms.Shared;

namespace Nms.Shared.Contracts;

// Request/response shapes shared by the API and the portal. Kept flat and explicit on purpose.
public sealed record SiteInput(string Name, string TimeZone = "UTC", VlanMode VlanMode = VlanMode.Routed,
    string? Address = null, double? Latitude = null, double? Longitude = null);
public sealed record AgentInput(string Name, int HeartbeatIntervalSec = 30);
public sealed record AgentCreated(Guid AgentId, string EnrollmentToken, DateTimeOffset ExpiresUtc);
public sealed record DeviceInput(string Name, string Address, int? VlanId, bool IsCritical = true, string[]? Tags = null, string? Notes = null, string? MacAddress = null);
public sealed record CheckInput(CheckType Type, int? Port = null, string? HttpPath = null, int? ExpectedHttpStatus = null, string? BodyRegex = null,
    int IntervalSec = 60, int TimeoutMs = 2000, int FailureThreshold = 3, bool Enabled = true, string? SnmpTemplate = null, Guid? SnmpCredentialId = null);
/// <summary>Secrets are write-only: blank on update keeps the stored value. SiteId null makes the credential usable at every site.</summary>
public sealed record SnmpCredentialInput(string Name, SnmpVersion Version, Guid? SiteId = null, string? Community = null, string? User = null,
    SnmpAuth Auth = SnmpAuth.None, string? AuthPassword = null, SnmpPriv Priv = SnmpPriv.None, string? PrivPassword = null, string? Context = null);
/// <summary>What the API returns for a credential: never the community or passwords.</summary>
public sealed record SnmpCredentialView(Guid Id, string Name, SnmpVersion Version, Guid? SiteId, string? User, SnmpAuth Auth, SnmpPriv Priv, string? Context, bool HasSecret, int Checks);
/// <summary>Instance null applies the limit to every row of a table metric (every interface, every sensor).</summary>
public sealed record ThresholdInput(string MetricKey, ThresholdOp Op, double Value, string Label, string? Instance = null, bool Enabled = true);
public sealed record MetricPoint(DateTimeOffset Time, double Value, double? Min = null, double? Max = null);
public sealed record ChannelInput(string Name, NotificationChannelType Type, string ConfigJson, bool Enabled = true);
public sealed record RuleInput(string Name, AlertScope Scope, Guid ChannelId, Guid? SiteId = null, Guid? DeviceId = null, string? Tag = null, bool CriticalOnly = true, int CooldownSec = 300, bool Enabled = true);
public sealed record MaintenanceInput(Guid? SiteId, Guid? DeviceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);
public sealed record ReleaseInput(string Version, string DownloadUrlX64, string DownloadUrlArm64, string Sha256X64, string Sha256Arm64, bool IsCurrent = true);
/// <summary>Publish a CI-built GitHub release; <c>Tag</c> null means the latest release.</summary>
public sealed record ReleaseImportRequest(string? Tag);
/// <summary>Device and check counts exclude the built-in Gateway device, whose Azure-side probe is reported separately.</summary>
public sealed record SiteSummary(Guid Id, string Name, int Devices, int Up, int Down, int Degraded, int Unknown, int AgentsOnline, int AgentsTotal, int OpenIncidents,
    CheckState? Gateway = null, int? GatewayLatencyMs = null, Guid? GatewayDeviceId = null, string? GatewayAddress = null);
public sealed record LatencyPoint(DateTimeOffset Bucket, long Samples, long OkSamples, double? AvgLatencyMs, int? MaxLatencyMs);

