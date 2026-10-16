namespace Nms.Shared;

public enum CheckType { Icmp = 0, TcpPort = 1, Http = 2 }

/// <summary>Evaluated state of a check as tracked by the worker's alert state machine.</summary>
public enum CheckState { Unknown = 0, Up = 1, Degraded = 2, Down = 3 }

public enum AgentStatus { PendingEnrollment = 0, Online = 1, Offline = 2, Revoked = 3 }

public enum IncidentKind { CheckDown = 0, AgentOffline = 1 }

public enum NotificationChannelType { Email = 0, TeamsWebhook = 1 }

public enum AlertScope { Global = 0, Site = 1, Tag = 2, Device = 3 }

/// <summary>How an agent reaches other VLANs at a site.</summary>
public enum VlanMode { Routed = 0, Trunked = 1 }

public enum Role { Viewer = 0, Operator = 1, Admin = 2 }
