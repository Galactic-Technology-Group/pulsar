namespace Nms.Worker;

public sealed class WorkerOptions
{
    /// <summary>How often to scan for un-notified incidents.</summary>
    public int NotifyPollSec { get; set; } = 10;
    /// <summary>Incidents opened within this window are batched into one message per channel.</summary>
    public int DigestWindowSec { get; set; } = 60;
    /// <summary>An agent is Offline when no heartbeat for this many heartbeat intervals.</summary>
    public int OfflineAfterHeartbeats { get; set; } = 3;
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
