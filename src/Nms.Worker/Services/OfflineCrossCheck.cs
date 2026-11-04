namespace Nms.Worker.Services;

public enum OfflineCause
{
    /// <summary>No Internet-check samples for the gap: the agent host, its power or its LAN link was down.</summary>
    NoData,
    /// <summary>The Internet check failed during the gap: the site's WAN line was down.</summary>
    InternetDown,
    /// <summary>The Internet check kept passing: the site was online, the agent-to-service path was not.</summary>
    InternetUp,
    /// <summary>Mixed samples: the line was flapping or came back before the agent reconnected.</summary>
    Partial,
}

public sealed record CrossCheckVerdict(OfflineCause Cause, int Samples, int Failed, DateTimeOffset? FirstFailure, DateTimeOffset? LastFailure)
{
    public string Text => Cause switch
    {
        OfflineCause.NoData => "no WAN probe data for the gap: agent host or its LAN link was down",
        OfflineCause.InternetDown => $"internet line was DOWN {FirstFailure:HH:mm}-{LastFailure:HH:mm} UTC ({Failed}/{Samples} probes failed)",
        OfflineCause.InternetUp => $"internet line stayed UP ({Samples} probes): agent-to-service path problem, not the site",
        OfflineCause.Partial => $"internet line partly down ({Failed}/{Samples} probes failed, first {FirstFailure:HH:mm}, last {LastFailure:HH:mm} UTC)",
        _ => "",
    };
}

/// <summary>
/// Explains an AgentOffline incident after the fact from the Internet-line samples the agent replays when it reconnects.
/// Pure so it can be unit-tested; the dispatcher feeds it the samples between the incident's open and close.
/// </summary>
public static class OfflineCrossCheck
{
    /// <summary>Samples must fall inside the incident window (callers add a little slack on both sides).</summary>
    public static CrossCheckVerdict Classify(IReadOnlyList<(DateTimeOffset Time, bool Ok)> samples)
    {
        if (samples.Count == 0) return new(OfflineCause.NoData, 0, 0, null, null);
        var failed = samples.Where(s => !s.Ok).ToList();
        if (failed.Count == 0) return new(OfflineCause.InternetUp, samples.Count, 0, null, null);
        var first = failed.Min(s => s.Time);
        var last = failed.Max(s => s.Time);
        // The line counts as down when at least 80 % of the samples failed; anything else is a flap or a partial outage.
        var cause = failed.Count * 5 >= samples.Count * 4 ? OfflineCause.InternetDown : OfflineCause.Partial;
        return new(cause, samples.Count, failed.Count, first, last);
    }
}
