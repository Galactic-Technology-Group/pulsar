using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nms.Agent.Transport;
using Nms.Shared.Contracts;

namespace Nms.Agent.Tests;

public class SelfUpdaterTests
{
    private static AgentVersionInfo Info(string v) => new(v, "https://example.invalid/x64", "https://example.invalid/arm64", new string('0', 64), new string('0', 64));

    private static SelfUpdater Updater(FakeTimeProvider clock) => Updater(clock, new FakeInstall(AgentVersion.Current));

    private static SelfUpdater Updater(FakeTimeProvider clock, FakeInstall box)
    {
        var opts = Options.Create(new AgentOptions { StateDir = box.State });
        var api = new ApiClient(opts, new CredentialStore(opts, NullLogger<CredentialStore>.Instance), NullLogger<ApiClient>.Instance);
        return new SelfUpdater(api, box.Guard(AgentVersion.Current), NullLogger<SelfUpdater>.Instance, clock);
    }

    [Fact]
    public void A_version_this_box_rolled_back_from_stays_blocked_until_another_is_published()
    {
        var box = new FakeInstall(AgentVersion.Current);
        File.WriteAllLines(Path.Combine(box.State, UpdateGuard.RollbackFile), ["failed=99.0.0", $"restored={AgentVersion.Current}", "reason=crash", "at=2026-11-09T03:00:00Z"]);
        var u = Updater(new FakeTimeProvider(), box);
        Assert.False(u.ShouldAttempt(Info("99.0.0")));
        Assert.True(u.ShouldAttempt(Info("99.0.1")));
    }

    [Fact]
    public void An_unconfirmed_update_is_never_stacked_on()
    {
        var box = new FakeInstall(AgentVersion.Current);
        File.WriteAllLines(box.StateFile, [$"version={AgentVersion.Current}", "previous=0.0.1", "starts=1"]);
        Assert.False(Updater(new FakeTimeProvider(), box).ShouldAttempt(Info("99.0.0")));
    }

    [Fact]
    public void Same_version_is_never_attempted()
    {
        var u = Updater(new FakeTimeProvider());
        Assert.False(u.ShouldAttempt(Info(AgentVersion.Current)));
        Assert.True(u.ShouldAttempt(Info("99.0.0")));
    }

    [Fact]
    public async Task Failed_version_is_not_retried_until_the_backoff_elapses()
    {
        var clock = new FakeTimeProvider();
        var u = Updater(clock);
        if (!OperatingSystem.IsLinux()) return;   // the download path (and therefore the failure) only runs on Linux

        await u.TryUpdateAsync(Info("99.0.0"), CancellationToken.None);   // example.invalid: download fails
        Assert.False(u.ShouldAttempt(Info("99.0.0")));
        Assert.True(u.ShouldAttempt(Info("99.0.1")));                      // a different version is still welcome

        clock.Advance(SelfUpdater.RetryAfterFailure + TimeSpan.FromSeconds(1));
        Assert.True(u.ShouldAttempt(Info("99.0.0")));
    }
}
