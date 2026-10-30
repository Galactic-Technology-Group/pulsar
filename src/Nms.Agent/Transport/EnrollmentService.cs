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
    /// <summary>True when an enrollment token is configured, i.e. rejected credentials can be replaced without operator help.</summary>
    public bool CanReEnroll => !string.IsNullOrWhiteSpace(options.Value.EnrollmentToken);

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
            catch (EnrollmentRejectedException ex)
            {
                // Retrying a dead token never helps, but the operator may re-issue one and restart us; keep the
                // service alive (and the message obvious) instead of crash-looping.
                log.LogError("{Message} Retrying in {Delay}.", ex.Message, delay);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Enrollment failed; retrying in {Delay}", delay);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
        }
    }

    /// <summary>
    /// Drops credentials the API no longer accepts (revoked, or the server's secret was re-issued) and enrols again
    /// with the configured token. Called by the hub service when a connect attempt comes back 401.
    /// </summary>
    public async Task<AgentCredentials> ReEnrollAsync(CancellationToken ct)
    {
        if (!CanReEnroll) throw new InvalidOperationException("Credentials were rejected and no EnrollmentToken is configured.");
        log.LogWarning("Stored credentials were rejected by the API; re-enrolling with the configured token");
        creds.Delete();
        return await EnsureEnrolledAsync(ct);
    }
}

/// <summary>The API answered an enrollment with 401: the token is expired, already used, or the agent is revoked.</summary>
public sealed class EnrollmentRejectedException(string message) : InvalidOperationException(message);
