using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nms.Agent;

public sealed record AgentCredentials(Guid AgentId, string Secret, Guid SiteId, string SiteName);

/// <summary>A claim-code enrollment in progress, kept across restarts so a reboot does not change the code on screen.</summary>
public sealed record PendingClaim(Guid ClaimId, string ClaimKey, string Code, DateTimeOffset ExpiresUtc);

/// <summary>
/// Persists the per-agent secret issued at enrollment to {StateDir}/credentials (mode 0600), the in-progress claim
/// to {StateDir}/claim.json (0600), and the human-readable code to {StateDir}/claim-code (0644) for the console
/// display service.
/// </summary>
public sealed class CredentialStore(IOptions<AgentOptions> options, ILogger<CredentialStore> log)
{
    private readonly string _path = Path.Combine(options.Value.StateDir, "credentials");
    private readonly string _claimPath = Path.Combine(options.Value.StateDir, "claim.json");
    private readonly string _codePath = Path.Combine(options.Value.StateDir, "claim-code");
    private AgentCredentials? _cached;

    public string ClaimCodePath => _codePath;

    public AgentCredentials? Load()
    {
        if (_cached is not null) return _cached;
        if (!File.Exists(_path)) return null;
        try
        {
            _cached = JsonSerializer.Deserialize<AgentCredentials>(File.ReadAllText(_path));
            return _cached;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Credentials file {Path} is unreadable", _path);
            return null;
        }
    }

    public void Save(AgentCredentials creds)
    {
        WritePrivate(_path, JsonSerializer.Serialize(creds));
        _cached = creds;
        DeleteClaim();
        log.LogInformation("Stored credentials for agent {AgentId}", creds.AgentId);
    }

    public void Delete()
    {
        _cached = null;
        if (File.Exists(_path)) File.Delete(_path);
    }

    public PendingClaim? LoadClaim()
    {
        if (!File.Exists(_claimPath)) return null;
        try { return JsonSerializer.Deserialize<PendingClaim>(File.ReadAllText(_claimPath)); }
        catch (Exception ex) { log.LogWarning(ex, "Claim file {Path} is unreadable; starting a new claim", _claimPath); return null; }
    }

    public void SaveClaim(PendingClaim claim)
    {
        WritePrivate(_claimPath, JsonSerializer.Serialize(claim));
        // World-readable on purpose: nms-agent-console (root) and a technician at the shell read it.
        Directory.CreateDirectory(Path.GetDirectoryName(_codePath)!);
        File.WriteAllText(_codePath, claim.Code + Environment.NewLine);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_codePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    public void DeleteClaim()
    {
        if (File.Exists(_claimPath)) File.Delete(_claimPath);
        if (File.Exists(_codePath)) File.Delete(_codePath);
    }

    private static void WritePrivate(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }
}
