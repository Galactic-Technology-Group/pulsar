using System.Globalization;
using Nms.Shared.Contracts;

namespace Nms.Agent.Transport;

/// <summary>
/// Makes a self-update reversible.
/// <list type="bullet">
/// <item>Before the swap the running binary is kept as <c>{exe}.prev</c> and <c>{state}/update.state</c> records the
/// pending version.</item>
/// <item>The new binary has to confirm itself (see <see cref="UpdateWatchdog"/>). Until it does, every start is counted
/// here and more than <see cref="MaxStarts"/> starts restore <c>.prev</c>.</item>
/// <item><c>deploy/agent/nms-agent-guard.sh</c>, run by systemd before every start, counts launches the same way for a
/// binary that cannot even reach <c>Main</c>.</item>
/// <item>A rollback is written to <c>{state}/rollback.state</c>. It blocks that version on this box, is reported in
/// every heartbeat, and is cleared when a later update confirms itself.</item>
/// </list>
/// Both files are <c>key=value</c> lines so the shell guard can read and write them without extra tools.
/// </summary>
public sealed class UpdateGuard
{
    public const string StateFile = "update.state";
    public const string RollbackFile = "rollback.state";
    /// <summary>Starts of an unconfirmed update before it is rolled back. The shell guard allows a few more launches.</summary>
    public const int MaxStarts = 3;

    private readonly string _exe, _prev, _statePath, _rollbackPath, _currentVersion;
    private readonly TimeProvider _clock;

    public UpdateGuard(string exePath, string stateDir, string currentVersion, TimeProvider? clock = null)
    {
        _exe = exePath;
        _prev = exePath + ".prev";
        _statePath = Path.Combine(stateDir, StateFile);
        _rollbackPath = Path.Combine(stateDir, RollbackFile);
        _currentVersion = currentVersion;
        _clock = clock ?? TimeProvider.System;
    }

    public static UpdateGuard ForThisProcess(string stateDir) =>
        new(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "nms-agent"), stateDir, AgentVersion.Current);

    public string PreviousBinaryPath => _prev;

    /// <summary>True while this binary is a self-update that has not confirmed itself yet.</summary>
    public bool IsPending => Read(_statePath) is { } s && s.GetValueOrDefault("version") == _currentVersion;

    /// <summary>The last rollback on this box, until a later update confirms itself.</summary>
    public UpdateRollbackDto? LastRollback
    {
        get
        {
            if (Read(_rollbackPath) is not { } r || r.GetValueOrDefault("failed") is not { Length: > 0 } failed) return null;
            var at = DateTimeOffset.TryParse(r.GetValueOrDefault("at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;
            var restored = r.GetValueOrDefault("restored");
            return new UpdateRollbackDto(failed, string.IsNullOrEmpty(restored) ? null : restored, r.GetValueOrDefault("reason") ?? "", at);
        }
    }

    /// <summary>Called by the updater just before it swaps the binary.</summary>
    public void BeginUpdate(string newVersion)
    {
        File.Copy(_exe, _prev, overwrite: true);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(_prev, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Write(_statePath, new()
        {
            ["version"] = newVersion, ["previous"] = _currentVersion, ["starts"] = "0", ["launches"] = "0", ["at"] = Now(),
        });
    }

    /// <summary>The swap failed after <see cref="BeginUpdate"/>: the running binary stays, so there is nothing to watch.</summary>
    public void AbandonUpdate() => TryDelete(_statePath);

    /// <summary>
    /// Call first thing at process start, before anything that could crash. Returns true when this start rolled back;
    /// the process should then exit so systemd starts the restored binary.
    /// </summary>
    public bool OnStartup()
    {
        if (Read(_statePath) is not { } s) return false;
        if (s.GetValueOrDefault("version") != _currentVersion)
        {
            TryDelete(_statePath);   // stale: the swap never happened, or the shell guard already restored another binary
            return false;
        }
        var starts = (int.TryParse(s.GetValueOrDefault("starts"), out var n) ? n : 0) + 1;
        if (starts > MaxStarts)
            return Revert($"agent {_currentVersion} stopped {starts - 1} times before confirming itself");
        s["starts"] = starts.ToString(CultureInfo.InvariantCulture);
        Write(_statePath, s);
        return false;
    }

    /// <summary>The new binary proved itself: stop watching, and forget an older rollback.</summary>
    public void Confirm()
    {
        if (!IsPending) return;
        TryDelete(_statePath);
        TryDelete(_rollbackPath);
    }

    /// <summary>Restores the previous binary. False when there is none (the update predates rollback support).</summary>
    public bool Revert(string reason)
    {
        var s = Read(_statePath);
        if (!File.Exists(_prev)) { TryDelete(_statePath); return false; }
        File.Move(_prev, _exe, overwrite: true);   // renaming over a running executable is safe on Linux
        Write(_rollbackPath, new()
        {
            ["failed"] = s?.GetValueOrDefault("version") ?? _currentVersion,
            ["restored"] = s?.GetValueOrDefault("previous") ?? "",
            ["reason"] = reason.ReplaceLineEndings(" "),
            ["at"] = Now(),
        });
        TryDelete(_statePath);
        return true;
    }

    private string Now() => _clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    internal static Dictionary<string, string>? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(path))
            {
                var i = line.IndexOf('=');
                if (i > 0) d[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
            return d;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void Write(string path, Dictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, values.Select(kv => $"{kv.Key}={kv.Value}"));
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

/// <summary>Whether the agent currently holds its hub connection.</summary>
public interface IHubStatus
{
    bool IsConnected { get; }
}

/// <summary>
/// Confirms a fresh self-update once the hub connection has held for <see cref="Probation"/>, or rolls it back when the
/// agent has not connected within <see cref="ConnectDeadline"/> although the API's health endpoint answers. When the
/// API is unreachable the update is not blamed (the site's WAN may be down) and the check repeats every
/// <see cref="Recheck"/>.
/// </summary>
public sealed class UpdateWatchdog(
    UpdateGuard guard,
    IHubStatus hub,
    Func<CancellationToken, Task<bool>> apiReachable,
    IHostApplicationLifetime lifetime,
    ILogger<UpdateWatchdog> log,
    TimeProvider? clock = null) : BackgroundService
{
    public static readonly TimeSpan Probation = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ConnectDeadline = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Recheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(15);

    public enum Outcome { Waiting, Confirmed, RolledBack, NothingToRestore }

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTimeOffset? _deadline, _connectedSince;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!guard.IsPending) return;
        log.LogInformation("Agent {Version} is a fresh self-update; it confirms itself after {Probation} connected", AgentVersion.Current, Probation);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(Poll, _clock, ct);
            switch (await TickAsync(ct))
            {
                case Outcome.Confirmed:
                    log.LogInformation("Agent {Version} confirmed; the previous binary is kept for manual rollback only", AgentVersion.Current);
                    return;
                case Outcome.RolledBack:
                    log.LogCritical("Agent {Version} did not connect within {Deadline} while the API answered; previous binary restored, restarting", AgentVersion.Current, ConnectDeadline);
                    lifetime.StopApplication();   // systemd (Restart=always) starts the restored binary
                    return;
                case Outcome.NothingToRestore:
                    log.LogError("Agent {Version} cannot reach the hub but there is no previous binary to restore", AgentVersion.Current);
                    return;
            }
        }
    }

    /// <summary>One evaluation; the loop calls it every 15 s. Separate from the loop so tests can drive it with a fake clock.</summary>
    internal async Task<Outcome> TickAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        _deadline ??= now + ConnectDeadline;

        if (hub.IsConnected)
        {
            _connectedSince ??= now;
            if (now - _connectedSince >= Probation) { guard.Confirm(); return Outcome.Confirmed; }
            return Outcome.Waiting;
        }
        _connectedSince = null;

        if (now < _deadline) return Outcome.Waiting;
        if (!await apiReachable(ct))
        {
            log.LogWarning("Agent {Version} has no hub connection and the API is unreachable too; not blaming the update, rechecking in {Recheck}", AgentVersion.Current, Recheck);
            _deadline = now + Recheck;
            return Outcome.Waiting;
        }
        return guard.Revert($"agent {AgentVersion.Current} could not connect to the hub within {ConnectDeadline.TotalMinutes:0} minutes while the API answered")
            ? Outcome.RolledBack
            : Outcome.NothingToRestore;
    }
}
