using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Nms.Agent.Network;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Transport;

/// <summary>
/// Ensures the agent holds valid credentials before anything else starts. Two paths:
/// <list type="bullet">
/// <item>Enrollment token configured (scripted installs): post it once, as before.</item>
/// <item>No token (ISO appliance, Pi image): ask the API for a claim code, show it on the console, poll until an
/// operator approves it in the portal. The code survives restarts and is replaced automatically when it expires.</item>
/// </list>
/// </summary>
public sealed class EnrollmentService(
    IOptions<AgentOptions> options,
    CredentialStore creds,
    ApiClient api,
    InterfaceDiscovery interfaces,
    ILogger<EnrollmentService> log)
{
    private bool HasToken => !string.IsNullOrWhiteSpace(options.Value.EnrollmentToken);

    /// <summary>Rejected credentials can always be replaced: with the token when one is configured, else by claiming again.</summary>
    public bool CanReEnroll => true;

    public async Task<AgentCredentials> EnsureEnrolledAsync(CancellationToken ct)
    {
        var existing = creds.Load();
        if (existing is not null) return existing;
        return HasToken ? await EnrollWithTokenAsync(ct) : await ClaimAsync(ct);
    }

    /// <summary>
    /// Drops credentials the API no longer accepts (revoked, or the server's secret was re-issued) and enrols again.
    /// Called by the hub service when a connect attempt comes back 401.
    /// </summary>
    public async Task<AgentCredentials> ReEnrollAsync(CancellationToken ct)
    {
        log.LogWarning(HasToken
            ? "Stored credentials were rejected by the API; re-enrolling with the configured token"
            : "Stored credentials were rejected by the API; requesting a new claim code");
        creds.Delete();
        return await EnsureEnrolledAsync(ct);
    }

    private async Task<AgentCredentials> EnrollWithTokenAsync(CancellationToken ct)
    {
        var token = options.Value.EnrollmentToken!;
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

    private async Task<AgentCredentials> ClaimAsync(CancellationToken ct)
    {
        var claim = creds.LoadClaim();
        var delay = TimeSpan.FromSeconds(ClaimAuth.PollIntervalSec);
        var failures = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (claim is null || claim.ExpiresUtc <= DateTimeOffset.UtcNow.AddSeconds(10))
                {
                    claim = await RequestClaimAsync(ct);
                    ShowCode(claim);
                }

                var poll = await api.PollClaimAsync(claim.ClaimId, claim.ClaimKey, ct);
                failures = 0;
                switch (poll.Outcome)
                {
                    case ApiClient.ClaimPollOutcome.Approved:
                        var r = poll.Credentials!;
                        var c = new AgentCredentials(r.AgentId, r.Secret, r.SiteId, r.SiteName);
                        creds.Save(c);   // also removes claim.json and claim-code
                        log.LogInformation("Claim {Code} approved: enrolled as agent {AgentId} at site {Site}", claim.Code, c.AgentId, c.SiteName);
                        Console.WriteLine($"\n  Pulsar agent enrolled at site \"{c.SiteName}\".\n");
                        return c;
                    case ApiClient.ClaimPollOutcome.Gone:
                        log.LogWarning("Claim {Code} expired or was rejected; requesting a new code", claim.Code);
                        creds.DeleteClaim();
                        claim = null;
                        continue;
                    default:
                        await Task.Delay(delay, ct);
                        continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                var wait = TimeSpan.FromSeconds(Math.Min(300, ClaimAuth.PollIntervalSec * Math.Pow(2, Math.Min(failures, 6))));
                log.LogWarning("Claim flow error ({Message}); retrying in {Wait}", ex.Message, wait);
                await Task.Delay(wait, ct);
            }
        }
    }

    private async Task<PendingClaim> RequestClaimAsync(CancellationToken ct)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var req = new ClaimRequest(key, Environment.MachineName, AgentVersion.Current, interfaces.Enumerate(), options.Value.Name);
        var created = await api.CreateClaimAsync(req, ct);
        var claim = new PendingClaim(created.ClaimId, key, created.Code, created.ExpiresUtc);
        creds.SaveClaim(claim);
        return claim;
    }

    private void ShowCode(PendingClaim claim)
    {
        var expires = claim.ExpiresUtc.ToUniversalTime().ToString("HH:mm 'UTC'");
        log.LogWarning("ENROLLMENT CODE {Code} (valid until {Expires}). Approve it in the portal under Pending agents.", claim.Code, expires);
        // Also to stdout for anyone running the binary interactively; the console unit reads {StateDir}/claim-code.
        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════════════╗");
        Console.WriteLine("  ║   PULSAR AGENT  -  waiting to be claimed      ║");
        Console.WriteLine($"  ║   Enrollment code:   {claim.Code,-10}               ║");
        Console.WriteLine($"  ║   Valid until {expires,-12}                    ║");
        Console.WriteLine("  ║   Portal → Pending agents → Approve           ║");
        Console.WriteLine("  ╚══════════════════════════════════════════════╝");
        Console.WriteLine();
    }
}

/// <summary>The API answered an enrollment with 401: the token is expired, already used, or the agent is revoked.</summary>
public sealed class EnrollmentRejectedException(string message) : InvalidOperationException(message);
