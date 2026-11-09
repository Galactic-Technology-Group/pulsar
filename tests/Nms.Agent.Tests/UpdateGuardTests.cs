using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nms.Agent.Transport;

namespace Nms.Agent.Tests;

/// <summary>A box with an installed binary: {dir}/nms-agent containing the text of the version that is "installed".</summary>
internal sealed class FakeInstall
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "nms-guard-tests", Guid.NewGuid().ToString("N"));
    public string Exe => Path.Combine(Dir, "nms-agent");
    public string State => Path.Combine(Dir, "state");

    public FakeInstall(string installed) { Directory.CreateDirectory(State); File.WriteAllText(Exe, installed); }

    public UpdateGuard Guard(string runningVersion, TimeProvider? clock = null) => new(Exe, State, runningVersion, clock);

    /// <summary>What the updater does: keep the running binary, mark the update pending, swap in the new one.</summary>
    public void Update(string from, string to)
    {
        Guard(from).BeginUpdate(to);
        File.WriteAllText(Exe, to);
    }

    public string Installed => File.ReadAllText(Exe);
    public string StateFile => Path.Combine(State, UpdateGuard.StateFile);
}

public class UpdateGuardTests
{
    [Fact]
    public void Begin_update_keeps_the_running_binary_and_marks_only_the_new_version_pending()
    {
        var box = new FakeInstall("1.0.0");
        box.Update("1.0.0", "1.1.0");

        Assert.Equal("1.0.0", File.ReadAllText(box.Exe + ".prev"));
        Assert.True(box.Guard("1.1.0").IsPending);
        Assert.False(box.Guard("1.0.0").IsPending);
    }

    [Fact]
    public void Too_many_starts_restore_the_previous_binary_and_record_why()
    {
        var box = new FakeInstall("1.0.0");
        box.Update("1.0.0", "1.1.0");
        var g = box.Guard("1.1.0", new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 3, 0, 0, TimeSpan.Zero)));

        for (var i = 0; i < UpdateGuard.MaxStarts; i++) Assert.False(g.OnStartup());
        Assert.True(g.OnStartup());

        Assert.Equal("1.0.0", box.Installed);
        Assert.False(File.Exists(box.Exe + ".prev"));
        Assert.False(File.Exists(box.StateFile));
        var rb = g.LastRollback!;
        Assert.Equal("1.1.0", rb.FailedVersion);
        Assert.Equal("1.0.0", rb.RestoredVersion);
        Assert.Contains("3 times", rb.Reason);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 3, 0, 0, TimeSpan.Zero), rb.AtUtc);
        Assert.Equal("1.1.0", box.Guard("1.0.0").LastRollback!.FailedVersion);   // the restored binary sees it too
    }

    [Fact]
    public void Confirm_stops_watching_and_forgets_an_older_rollback()
    {
        var box = new FakeInstall("1.0.0");
        File.WriteAllLines(Path.Combine(box.State, UpdateGuard.RollbackFile), ["failed=0.9.0", "restored=0.8.0", "reason=x", "at=2026-11-01T00:00:00Z"]);
        box.Update("1.0.0", "1.1.0");
        var g = box.Guard("1.1.0");

        g.Confirm();

        Assert.False(g.IsPending);
        Assert.Null(g.LastRollback);
        Assert.True(File.Exists(box.Exe + ".prev"));   // kept for a manual rollback
    }

    [Fact]
    public void A_marker_for_another_version_is_stale_and_dropped()
    {
        var box = new FakeInstall("1.0.0");
        box.Guard("1.0.0").BeginUpdate("1.1.0");          // swap never happened: still running 1.0.0
        Assert.False(box.Guard("1.0.0").OnStartup());
        Assert.False(File.Exists(box.StateFile));
    }

    [Fact]
    public void Revert_without_a_previous_binary_reports_false_and_stops_counting()
    {
        var box = new FakeInstall("1.1.0");
        File.WriteAllLines(box.StateFile, ["version=1.1.0", "previous=1.0.0", "starts=0"]);
        var g = box.Guard("1.1.0");
        Assert.False(g.Revert("test"));
        Assert.Equal("1.1.0", box.Installed);
        Assert.False(g.IsPending);
        Assert.Null(g.LastRollback);
    }

    [Fact]
    public void Start_counting_keeps_the_shell_guards_launch_count()
    {
        var box = new FakeInstall("1.0.0");
        box.Update("1.0.0", "1.1.0");
        File.AppendAllText(box.StateFile, "launches=2\n".Replace("\n", Environment.NewLine));
        box.Guard("1.1.0").OnStartup();
        var s = UpdateGuard.Read(box.StateFile)!;
        Assert.Equal("2", s["launches"]);
        Assert.Equal("1", s["starts"]);
    }
}

public class UpdateWatchdogTests
{
    private sealed class Hub : IHubStatus { public bool IsConnected { get; set; } }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }

    private static (UpdateWatchdog dog, FakeInstall box, Hub hub, FakeTimeProvider clock) Setup(Func<bool> apiReachable)
    {
        var box = new FakeInstall("1.0.0");
        box.Update("1.0.0", "1.1.0");
        var clock = new FakeTimeProvider();
        var hub = new Hub();
        var dog = new UpdateWatchdog(box.Guard("1.1.0", clock), hub, _ => Task.FromResult(apiReachable()), new Lifetime(), NullLogger<UpdateWatchdog>.Instance, clock);
        return (dog, box, hub, clock);
    }

    [Fact]
    public async Task Confirms_once_the_connection_has_held_for_the_probation()
    {
        var (dog, box, hub, clock) = Setup(() => true);
        hub.IsConnected = true;
        Assert.Equal(UpdateWatchdog.Outcome.Waiting, await dog.TickAsync(default));
        clock.Advance(UpdateWatchdog.Probation - TimeSpan.FromSeconds(1));
        Assert.Equal(UpdateWatchdog.Outcome.Waiting, await dog.TickAsync(default));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(UpdateWatchdog.Outcome.Confirmed, await dog.TickAsync(default));
        Assert.False(box.Guard("1.1.0").IsPending);
        Assert.Equal("1.1.0", box.Installed);
    }

    [Fact]
    public async Task A_dropped_connection_restarts_the_probation()
    {
        var (dog, _, hub, clock) = Setup(() => true);
        hub.IsConnected = true; await dog.TickAsync(default);
        clock.Advance(TimeSpan.FromMinutes(1.5)); hub.IsConnected = false; await dog.TickAsync(default);
        clock.Advance(TimeSpan.FromSeconds(15)); hub.IsConnected = true; await dog.TickAsync(default);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(UpdateWatchdog.Outcome.Waiting, await dog.TickAsync(default));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(UpdateWatchdog.Outcome.Confirmed, await dog.TickAsync(default));
    }

    [Fact]
    public async Task Rolls_back_when_the_hub_never_connects_while_the_api_answers()
    {
        var (dog, box, _, clock) = Setup(() => true);
        Assert.Equal(UpdateWatchdog.Outcome.Waiting, await dog.TickAsync(default));
        clock.Advance(UpdateWatchdog.ConnectDeadline);
        Assert.Equal(UpdateWatchdog.Outcome.RolledBack, await dog.TickAsync(default));
        Assert.Equal("1.0.0", box.Installed);
        Assert.Contains("could not connect", box.Guard("1.0.0").LastRollback!.Reason);
    }

    [Fact]
    public async Task An_unreachable_api_is_not_blamed_on_the_update()
    {
        var reachable = false;
        var (dog, box, _, clock) = Setup(() => reachable);
        await dog.TickAsync(default);
        clock.Advance(UpdateWatchdog.ConnectDeadline);
        Assert.Equal(UpdateWatchdog.Outcome.Waiting, await dog.TickAsync(default));   // WAN down: wait

        reachable = true;
        clock.Advance(UpdateWatchdog.Recheck - TimeSpan.FromSeconds(1));
        Assert.Equal(UpdateWatchdog.Outcome.Waiting, await dog.TickAsync(default));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(UpdateWatchdog.Outcome.RolledBack, await dog.TickAsync(default));
        Assert.Equal("1.0.0", box.Installed);
    }
}
