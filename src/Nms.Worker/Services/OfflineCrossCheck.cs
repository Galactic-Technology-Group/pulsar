namespace Nms.Worker.Services;

public enum OfflineCause
{
    /// <summary>No gateway probes for the gap: the site has no gateway address, or the check is disabled.</summary>
    NoData,
    /// <summary>The gateway was unreachable from Azure during the gap: the site's WAN was down.</summary>
    GatewayDown,
    /// <summary>The gateway kept answering from Azure: the site was online, the agent host or its LAN was not.</summary>
    GatewayUp,
    /// <summary>Mixed samples: the line was flapping or came back before the agent did.</summary>
    Partial,
}

public sealed record CrossCheckVerdict(OfflineCause Cause, int Samples, int Failed, DateTimeOffset? FirstFailure, DateTimeOffset? LastFailure)
{
    public string Text => Cause switch
    {
        OfflineCause.NoData => "no gateway probes from Azure for the gap (set the site's gateway address to get a cross-check)",
        OfflineCause.GatewayDown => $"gateway was unreachable from Azure {FirstFailure:HH:mm}-{LastFailure:HH:mm} UTC ({Failed}/{Samples} probes): site WAN was down",
        OfflineCause.GatewayUp => $"gateway stayed reachable from Azure ({Samples} probes): agent host or LAN problem, not the site line",
        OfflineCause.Partial => $"gateway partly unreachable from Azure ({Failed}/{Samples} probes, first {FirstFailure:HH:mm}, last {LastFailure:HH:mm} UTC)",
        _ => "",
    };
}

/// <summary>
/// Explains an AgentOffline incident from the gateway probes the worker took from Azure while the agent was gone.
/// Pure so it can be unit-tested; the dispatcher feeds it the samples between the incident's open and close.
/// </summary>
public static class OfflineCrossCheck
{
    public static CrossCheckVerdict Classify(IReadOnlyList<(DateTimeOffset Time, bool Ok)> samples)
    {
        if (samples.Count == 0) return new(OfflineCause.NoData, 0, 0, null, null);
        var failed = samples.Where(s => !s.Ok).ToList();
        if (failed.Count == 0) return new(OfflineCause.GatewayUp, samples.Count, 0, null, null);
        var first = failed.Min(s => s.Time);
        var last = failed.Max(s => s.Time);
        // The line counts as down when at least 80 % of the samples failed; anything else is a flap or a partial outage.
        var cause = failed.Count * 5 >= samples.Count * 4 ? OfflineCause.GatewayDown : OfflineCause.Partial;
        return new(cause, samples.Count, failed.Count, first, last);
    }
}
