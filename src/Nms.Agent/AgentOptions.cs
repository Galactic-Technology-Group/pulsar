namespace Nms.Agent;

/// <summary>Bound from /etc/nms-agent/agent.json (section-less root) and NMS_* environment variables.</summary>
public sealed class AgentOptions
{
    public const string DefaultConfigPath = "/etc/nms-agent/agent.json";
    public const string DefaultStateDir = "/var/lib/nms-agent";

    public string ApiBaseUrl { get; set; } = "https://localhost:5001";
    /// <summary>One-time token; only read until enrollment succeeds.</summary>
    public string? EnrollmentToken { get; set; }
    public string? Name { get; set; }
    public string StateDir { get; set; } = DefaultStateDir;
    /// <summary>Optional explicit interface list. Empty = auto-detect all non-loopback IPv4 interfaces.</summary>
    public List<string> Interfaces { get; set; } = new();
    public int MaxConcurrentIcmp { get; set; } = 64;
    public int MaxConcurrentTcp { get; set; } = 32;
    public int MaxConcurrentHttp { get; set; } = 16;
    /// <summary>Flush outbox when this many results are queued, regardless of the timer.</summary>
    public int ResultsBatchSize { get; set; } = 500;
    /// <summary>Accept self-signed API certs. Dev only.</summary>
    public bool DangerousAcceptAnyServerCertificate { get; set; }
}
