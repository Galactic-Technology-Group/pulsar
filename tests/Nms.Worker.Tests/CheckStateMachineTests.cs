using Nms.Shared;

namespace Nms.Worker.Tests;

public class CheckStateMachineTests
{
    [Fact]
    public void Unknown_goes_up_on_first_success()
    {
        var t = CheckStateMachine.Apply(CheckState.Unknown, 0, 3, ok: true);
        Assert.Equal(CheckState.Up, t.Current);
        Assert.Equal(0, t.ConsecutiveFailures);
        Assert.True(t.Changed);
        Assert.False(t.WentDown);
        Assert.False(t.Recovered);
    }

    [Fact]
    public void Failures_below_threshold_are_degraded_not_down()
    {
        var t1 = CheckStateMachine.Apply(CheckState.Up, 0, 3, ok: false);
        Assert.Equal(CheckState.Degraded, t1.Current);
        Assert.Equal(1, t1.ConsecutiveFailures);

        var t2 = CheckStateMachine.Apply(t1.Current, t1.ConsecutiveFailures, 3, ok: false);
        Assert.Equal(CheckState.Degraded, t2.Current);
        Assert.Equal(2, t2.ConsecutiveFailures);
        Assert.False(t2.WentDown);
    }

    [Fact]
    public void Threshold_reached_goes_down_exactly_once()
    {
        var t3 = CheckStateMachine.Apply(CheckState.Degraded, 2, 3, ok: false);
        Assert.Equal(CheckState.Down, t3.Current);
        Assert.True(t3.WentDown);

        var t4 = CheckStateMachine.Apply(t3.Current, t3.ConsecutiveFailures, 3, ok: false);
        Assert.Equal(CheckState.Down, t4.Current);
        Assert.False(t4.WentDown);
        Assert.False(t4.Changed);
        Assert.Equal(4, t4.ConsecutiveFailures);
    }

    [Fact]
    public void Success_after_down_is_recovery()
    {
        var t = CheckStateMachine.Apply(CheckState.Down, 7, 3, ok: true);
        Assert.Equal(CheckState.Up, t.Current);
        Assert.True(t.Recovered);
        Assert.Equal(0, t.ConsecutiveFailures);
    }

    [Fact]
    public void Success_after_degraded_is_not_a_recovery_event()
    {
        var t = CheckStateMachine.Apply(CheckState.Degraded, 1, 3, ok: true);
        Assert.Equal(CheckState.Up, t.Current);
        Assert.False(t.Recovered);
        Assert.True(t.Changed);
    }

    [Fact]
    public void Threshold_of_one_goes_straight_down()
    {
        var t = CheckStateMachine.Apply(CheckState.Up, 0, 1, ok: false);
        Assert.Equal(CheckState.Down, t.Current);
        Assert.True(t.WentDown);
    }
}
