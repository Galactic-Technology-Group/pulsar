namespace Nms.Worker;

public sealed class WorkerOptions
{
    /// <summary>How often to scan for un-notified incidents.</summary>
    public int NotifyPollSec { get; set; } = 10;
    /// <summary>Incidents opened within this window are batched into one message per channel.</summary>
    public int DigestWindowSec { get; set; } = 60;
    /// <summary>Recoveries wait this long after the incident closes before they are sent. 0 sends on the next pass.</summary>
    public int RecoveryGraceSec { get; set; } = 0;
    /// <summary>How often the worker looks for due gateway probes; each gateway check keeps its own interval (30 s by default).</summary>
    public int GatewayPollSec { get; set; } = 10;
    /// <summary>
    /// Allow gateway checks without a port to use ICMP. Off by default: Azure Container Apps egress (Standard Load
    /// Balancer / NAT Gateway) never returns echo replies, so a ping there would mark every gateway down. Turn on only
    /// where the worker runs with a real outbound path for ICMP (a VM with a public IP, on-premises).
    /// </summary>
    public bool GatewayIcmpEnabled { get; set; } = false;
    /// <summary>
    /// An agent is Offline when no heartbeat for this many heartbeat intervals. With 30 s heartbeats, 6 means a site is
    /// declared unreachable after 3 minutes: a dropped SignalR socket costs the agent up to ~90 s to notice and reconnect
    /// through its backoff, so anything under that alerts on ordinary WAN blips.
    /// </summary>
    public int OfflineAfterHeartbeats { get; set; } = 6;
    public int WatchdogPollSec { get; set; } = 15;
    public int RetentionDays { get; set; } = 90;
    public int IncidentRetentionDays { get; set; } = 365;
    /// <summary>Public portal URL used in notification links.</summary>
    public string PortalBaseUrl { get; set; } = "";
}

public sealed class EmailOptions
{
    public string ConnectionString { get; set; } = "";
    public string From { get; set; } = "";
}
