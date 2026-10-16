using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nms.Agent;

public sealed record AgentCredentials(Guid AgentId, string Secret, Guid SiteId, string SiteName);

/// <summary>Persists the per-agent secret issued at enrollment to {StateDir}/credentials (mode 0600).</summary>
public sealed class CredentialStore(IOptions<AgentOptions> options, ILogger<CredentialStore> log)
{
    private readonly string _path = Path.Combine(options.Value.StateDir, "credentials");
    private AgentCredentials? _cached;

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
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(creds));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _path, overwrite: true);
        _cached = creds;
        log.LogInformation("Stored credentials for agent {AgentId}", creds.AgentId);
    }

    public void Delete()
    {
        _cached = null;
        if (File.Exists(_path)) File.Delete(_path);
    }
}
