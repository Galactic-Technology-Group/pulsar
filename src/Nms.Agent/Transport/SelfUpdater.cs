using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nms.Shared.Contracts;

namespace Nms.Agent.Transport;

/// <summary>
/// Downloads a new binary next to the running one, verifies SHA-256, swaps it in and exits so systemd restarts us.
/// Only acts when the version differs and the file hash matches; otherwise it is a no-op.
/// </summary>
public sealed class SelfUpdater(ApiClient api, ILogger<SelfUpdater> log)
{
    private int _inProgress;

    public async Task TryUpdateAsync(AgentVersionInfo v, CancellationToken ct)
    {
        if (string.Equals(v.Version, AgentVersion.Current, StringComparison.OrdinalIgnoreCase)) return;
        if (!OperatingSystem.IsLinux()) { log.LogInformation("Self-update is Linux-only; skipping {Version}", v.Version); return; }
        if (Interlocked.Exchange(ref _inProgress, 1) == 1) return;

        try
        {
            var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            var url = arm ? v.DownloadUrlArm64 : v.DownloadUrlX64;
            var expected = (arm ? v.Sha256Arm64 : v.Sha256X64).Replace(":", "").ToLowerInvariant();
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath unknown");
            var tmp = exe + ".new";

            log.LogInformation("Downloading agent {Version} from {Url}", v.Version, url);
            await api.DownloadAsync(url, tmp, ct);

            await using (var fs = File.OpenRead(tmp))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (actual != expected)
                {
                    File.Delete(tmp);
                    log.LogError("Hash mismatch for {Version}: expected {Expected}, got {Actual}", v.Version, expected, actual);
                    return;
                }
            }

            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(tmp, exe, overwrite: true);
            log.LogWarning("Agent updated to {Version}; exiting for systemd restart", v.Version);
            Environment.Exit(0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Self-update failed");
        }
        finally { Interlocked.Exchange(ref _inProgress, 0); }
    }
}
