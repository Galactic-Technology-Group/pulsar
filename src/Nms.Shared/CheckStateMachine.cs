namespace Nms.Shared;

public sealed record StateTransition(CheckState Previous, CheckState Current, int ConsecutiveFailures)
{
    public bool WentDown => Previous != CheckState.Down && Current == CheckState.Down;
    public bool Recovered => Previous == CheckState.Down && Current == CheckState.Up;
    public bool Changed => Previous != Current;
}

/// <summary>
/// Pure transition logic: Up → Degraded (1..threshold-1 consecutive failures) → Down (threshold reached) → Up on first success.
/// Unknown behaves like Up for the purpose of the first transition.
/// </summary>
public static class CheckStateMachine
{
    public static StateTransition Apply(CheckState current, int consecutiveFailures, int failureThreshold, bool ok)
    {
        failureThreshold = Math.Max(1, failureThreshold);
        if (ok)
            return new StateTransition(current, CheckState.Up, 0);

        var failures = consecutiveFailures + 1;
        var next = failures >= failureThreshold ? CheckState.Down : CheckState.Degraded;
        // Once Down, stay Down until a success; never regress to Degraded.
        if (current == CheckState.Down) next = CheckState.Down;
        return new StateTransition(current, next, failures);
    }
}
