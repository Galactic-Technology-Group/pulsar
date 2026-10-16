using Microsoft.Extensions.Options;
using Nms.Agent.Network;
using Nms.Shared.Contracts;

namespace Nms.Agent.Transport;

/// <summary>Ensures the agent holds valid credentials before anything else starts.</summary>
public sealed class EnrollmentService(
    IOptions<AgentOptions> options,
    CredentialStore creds,
    ApiClient api,
    InterfaceDiscovery interfaces,
    ILogger<EnrollmentService> log)
{
    public async Task<AgentCredentials> EnsureEnrolledAsync(CancellationToken ct)
    {
        var existing = creds.Load();
        if (existing is not null) return existing;

        var token = options.Value.EnrollmentToken;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"No credentials in {options.Value.StateDir} and no EnrollmentToken configured.");

        var delay = TimeSpan.FromSeconds(5);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var req = new EnrollRequest(token, Environment.MachineName, AgentVersion.Current, interfaces.Enumerate());
                var resp = await api.EnrollAsync(req, ct);
                var c = new AgentCredentials(resp.AgentId, resp.Secret, resp.SiteId, resp.SiteName);
                creds.Save(c);
                log.LogInformation("Enrolled as agent {AgentId} at site {Site}", c.AgentId, c.SiteName);
                return c;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Enrollment failed; retrying in {Delay}", delay);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
        }
    }
}
