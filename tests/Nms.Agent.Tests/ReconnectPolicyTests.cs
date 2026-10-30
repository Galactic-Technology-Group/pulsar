using Microsoft.AspNetCore.SignalR.Client;
using Nms.Agent.Transport;

namespace Nms.Agent.Tests;

public class ReconnectPolicyTests
{
    private static TimeSpan? Delay(long previous) =>
        new HubConnectionService.BackoffRetryPolicy().NextRetryDelay(new RetryContext { PreviousRetryCount = previous, ElapsedTime = TimeSpan.Zero });

    [Fact]
    public void Backs_off_exponentially_and_caps_at_two_minutes()
    {
        Assert.InRange(Delay(0)!.Value.TotalSeconds, 1, 2);
        Assert.InRange(Delay(3)!.Value.TotalSeconds, 8, 9);
        Assert.InRange(Delay(7)!.Value.TotalSeconds, 120, 121);
        Assert.InRange(Delay(9)!.Value.TotalSeconds, 120, 121);
    }

    [Fact]
    public void Gives_up_so_the_service_can_rebuild_and_re_enrol()
    {
        // Null tells SignalR to stop reconnecting and raise Closed; HubConnectionService then starts a fresh
        // connect loop, which is where rejected credentials are swapped for a new enrolment.
        Assert.Null(Delay(HubConnectionService.BackoffRetryPolicy.MaxAttempts));
        Assert.NotNull(Delay(HubConnectionService.BackoffRetryPolicy.MaxAttempts - 1));
    }
}
