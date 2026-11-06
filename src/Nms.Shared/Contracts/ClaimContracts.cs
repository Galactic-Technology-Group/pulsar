namespace Nms.Shared.Contracts;

/// <summary>
/// Claim-code enrollment ("device code" flow). An agent with no credentials and no enrollment token asks the API
/// for a claim, shows the short code on its console, and polls until an operator approves the claim in the portal
/// and assigns it to a site. The claim key is a random secret the agent keeps to itself: only the box that
/// requested the code can collect the credentials, so someone who merely sees the code on a screen cannot.
/// </summary>
public static class ClaimAuth
{
    /// <summary>Header carrying the claim key on poll requests.</summary>
    public const string ClaimKeyHeader = "X-Nms-Claim-Key";
    public const int CodeLifetimeMinutes = 15;
    public const int PollIntervalSec = 5;
}

public sealed record ClaimRequest(string ClaimKey, string Hostname, string AgentVersion, IReadOnlyList<InterfaceInfo> Interfaces, string? RequestedName);

public sealed record ClaimCreated(Guid ClaimId, string Code, DateTimeOffset ExpiresUtc, int PollIntervalSec);

/// <summary>Body of a 202 while the claim is still waiting for an operator.</summary>
public sealed record ClaimPending(ClaimState State, DateTimeOffset ExpiresUtc);

/// <summary>What the portal shows on the Pending agents page.</summary>
public sealed record PendingClaimDto(
    Guid Id,
    string Code,
    string Hostname,
    string? AgentVersion,
    string? RequestedName,
    IReadOnlyList<InterfaceInfo> Interfaces,
    string? RemoteAddress,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc);

public sealed record ClaimApproveInput(Guid SiteId, string Name, int HeartbeatIntervalSec = 30);
