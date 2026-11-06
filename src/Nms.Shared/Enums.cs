namespace Nms.Shared;

/// <summary>GatewayPing is probed by the worker from Azure, never by an agent: the outside-in cross-check of a site.</summary>
public enum CheckType { Icmp = 0, TcpPort = 1, Http = 2, GatewayPing = 3 }

/// <summary>Host is an ordinary monitored device. Gateway is the per-site built-in device holding the site's public address.</summary>
public enum DeviceKind { Host = 0, Gateway = 1 }

/// <summary>Evaluated state of a check as tracked by the worker's alert state machine.</summary>
public enum CheckState { Unknown = 0, Up = 1, Degraded = 2, Down = 3 }

public enum AgentStatus { PendingEnrollment = 0, Online = 1, Offline = 2, Revoked = 3 }

public enum IncidentKind { CheckDown = 0, AgentOffline = 1, GatewayDown = 2 }

public enum NotificationChannelType { Email = 0, TeamsWebhook = 1 }

public enum AlertScope { Global = 0, Site = 1, Tag = 2, Device = 3 }

/// <summary>How an agent reaches other VLANs at a site.</summary>
public enum VlanMode { Routed = 0, Trunked = 1 }

public enum Role { Viewer = 0, Operator = 1, Admin = 2 }

/// <summary>
/// Lifecycle of a claim code shown on an unenrolled agent's console. Approved means an operator assigned it to a
/// site; Collected means the agent has since fetched its credentials and the claim is finished.
/// </summary>
public enum ClaimState { Pending = 0, Approved = 1, Collected = 2, Rejected = 3, Expired = 4 }
