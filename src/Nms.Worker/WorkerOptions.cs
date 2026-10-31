namespace Nms.Worker;

public sealed class WorkerOptions
{
    /// <summary>How often to scan for un-notified incidents.</summary>
    public int NotifyPollSec { get; set; } = 10;
    /// <summary>Incidents opened within this window are batched into one message per channel.</summary>
    public int DigestWindowSec { get; set; } = 60;
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
