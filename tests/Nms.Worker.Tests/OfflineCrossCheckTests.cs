using Nms.Worker.Services;

namespace Nms.Worker.Tests;

public class OfflineCrossCheckTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 14, 0, 0, TimeSpan.Zero);
    private static List<(DateTimeOffset, bool)> Samples(params bool[] oks) =>
        oks.Select((ok, i) => (T0.AddSeconds(30 * i), ok)).ToList();

    [Fact]
    public void No_samples_means_no_gateway_configured()
    {
        var v = OfflineCrossCheck.Classify([]);
        Assert.Equal(OfflineCause.NoData, v.Cause);
        Assert.Contains("gateway address", v.Text);
    }

    [Fact]
    public void All_failures_mean_the_site_line_was_down()
    {
        var v = OfflineCrossCheck.Classify(Samples(false, false, false, false, false));
        Assert.Equal(OfflineCause.GatewayDown, v.Cause);
        Assert.Equal(T0, v.FirstFailure);
        Assert.Equal(T0.AddSeconds(120), v.LastFailure);
        Assert.Contains("14:00-14:02", v.Text);
        Assert.Contains("site WAN was down", v.Text);
    }

    [Fact]
    public void Mostly_failures_with_a_recovery_tail_still_count_as_line_down()
    {
        // 4 of 5 failed (80 %): the line came back just before the agent did.
        var v = OfflineCrossCheck.Classify(Samples(false, false, false, false, true));
        Assert.Equal(OfflineCause.GatewayDown, v.Cause);
    }

    [Fact]
    public void All_passes_mean_the_problem_was_not_the_site()
    {
        var v = OfflineCrossCheck.Classify(Samples(true, true, true, true));
        Assert.Equal(OfflineCause.GatewayUp, v.Cause);
        Assert.Contains("not the site line", v.Text);
    }

    [Fact]
    public void Mixed_samples_are_reported_as_partial()
    {
        var v = OfflineCrossCheck.Classify(Samples(true, false, true, false, true, true));
        Assert.Equal(OfflineCause.Partial, v.Cause);
        Assert.Equal(2, v.Failed);
        Assert.Equal(6, v.Samples);
    }
}
