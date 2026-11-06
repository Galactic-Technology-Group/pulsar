using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nms.Api.Auth;
using Nms.Api.Hubs;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;
using Nms.Shared.Contracts;
using Nms.Shared.Hubs;

namespace Nms.Api.Services;

/// <summary>
/// Claim-code enrollment. Create → (operator approves, which creates the Agent row) → the agent collects its secret
/// on its next poll. The secret is generated at collection and never persisted in plaintext.
/// </summary>
public sealed class ClaimService(NmsDbContext db, IHubContext<PortalHub, IPortalClient> portal, ILogger<ClaimService> log)
{
    public enum PollOutcome { NotFound, Pending, Approved, Gone }
    public sealed record PollResult(PollOutcome Outcome, EnrollResponse? Credentials = null, DateTimeOffset? ExpiresUtc = null);

    public async Task<ClaimCreated> CreateAsync(ClaimRequest req, string? remoteAddress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ClaimKey) || req.ClaimKey.Length < 32)
            throw new ClaimException("ClaimKey must be at least 32 characters", StatusCodes.Status400BadRequest);
        if (string.IsNullOrWhiteSpace(req.Hostname))
            throw new ClaimException("Hostname is required", StatusCodes.Status400BadRequest);

        await PruneAsync(ct);

        var now = DateTimeOffset.UtcNow;
        string code;
        var attempts = 0;
        do
        {
            code = ClaimCode.Generate();
            attempts++;
        } while (await db.AgentClaims.AnyAsync(c => c.Code == code && c.State == ClaimState.Pending && c.ExpiresUtc > now, ct) && attempts < 10);

        var claim = new AgentClaim
        {
            Id = Guid.NewGuid(),
            Code = code,
            ClaimKeyHash = SecretHasher.Sha256(req.ClaimKey),
            Hostname = req.Hostname.Trim()[..Math.Min(req.Hostname.Trim().Length, 255)],
            AgentVersion = req.AgentVersion,
            RequestedName = string.IsNullOrWhiteSpace(req.RequestedName) ? null : req.RequestedName.Trim(),
            InterfacesJson = JsonSerializer.Serialize(req.Interfaces, NmsJson.Options),
            RemoteAddress = remoteAddress,
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(ClaimAuth.CodeLifetimeMinutes),
        };
        db.AgentClaims.Add(claim);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Claim {Code} created for host {Host} from {Remote}", ClaimCode.Format(code), claim.Hostname, remoteAddress);
        await NotifyAsync();
        return new ClaimCreated(claim.Id, ClaimCode.Format(code), claim.ExpiresUtc, ClaimAuth.PollIntervalSec);
    }

    public async Task<PollResult> PollAsync(Guid claimId, string? claimKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(claimKey)) return new PollResult(PollOutcome.NotFound);
        var claim = await db.AgentClaims.FirstOrDefaultAsync(c => c.Id == claimId, ct);
        if (claim is null || !FixedTimeEquals(claim.ClaimKeyHash, SecretHasher.Sha256(claimKey))) return new PollResult(PollOutcome.NotFound);

        var now = DateTimeOffset.UtcNow;
        switch (claim.State)
        {
            case ClaimState.Pending when claim.ExpiresUtc <= now:
                claim.State = ClaimState.Expired;
                await db.SaveChangesAsync(ct);
                await NotifyAsync();
                return new PollResult(PollOutcome.Gone);
            case ClaimState.Pending:
                return new PollResult(PollOutcome.Pending, ExpiresUtc: claim.ExpiresUtc);
            case ClaimState.Approved:
            {
                var agent = await db.Agents.Include(a => a.Site).FirstOrDefaultAsync(a => a.Id == claim.AgentId, ct);
                if (agent is null || agent.Status == AgentStatus.Revoked)
                {
                    claim.State = ClaimState.Rejected;
                    await db.SaveChangesAsync(ct);
                    return new PollResult(PollOutcome.Gone);
                }
                var secret = SecretHasher.NewToken(32);
                agent.SecretHash = SecretHasher.HashSecret(secret);
                agent.Status = AgentStatus.Offline;
                agent.LastSeenUtc = now;
                agent.EnrollmentTokenHash = null;
                agent.EnrollmentTokenExpiresUtc = null;
                claim.State = ClaimState.Collected;
                await db.SaveChangesAsync(ct);
                log.LogInformation("Claim {Code} collected: agent {AgentId} ({Host}) enrolled at site {Site}", ClaimCode.Format(claim.Code), agent.Id, agent.Hostname, agent.Site.Name);
                await NotifyAsync();
                return new PollResult(PollOutcome.Approved, new EnrollResponse(agent.Id, secret, agent.SiteId, agent.Site.Name));
            }
            default:
                return new PollResult(PollOutcome.Gone);
        }
    }

    public async Task<List<PendingClaimDto>> ListPendingAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.AgentClaims.AsNoTracking()
            .Where(c => c.State == ClaimState.Pending && c.ExpiresUtc > now)
            .OrderBy(c => c.CreatedUtc).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Agent> ApproveAsync(Guid claimId, ClaimApproveInput input, string approvedBy, CancellationToken ct)
    {
        var claim = await db.AgentClaims.FirstOrDefaultAsync(c => c.Id == claimId, ct)
            ?? throw new ClaimException("Claim not found", StatusCodes.Status404NotFound);
        var now = DateTimeOffset.UtcNow;
        if (claim.State != ClaimState.Pending) throw new ClaimException($"Claim is {claim.State}", StatusCodes.Status409Conflict);
        if (claim.ExpiresUtc <= now)
        {
            claim.State = ClaimState.Expired; await db.SaveChangesAsync(ct);
            throw new ClaimException("Claim expired; the agent will show a new code", StatusCodes.Status410Gone);
        }
        var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == input.SiteId, ct)
            ?? throw new ClaimException("Site not found", StatusCodes.Status404NotFound);
        var name = string.IsNullOrWhiteSpace(input.Name) ? claim.RequestedName ?? claim.Hostname : input.Name.Trim();
        if (await db.Agents.AnyAsync(a => a.SiteId == site.Id && a.Name == name, ct))
            throw new ClaimException($"An agent named '{name}' already exists at {site.Name}", StatusCodes.Status409Conflict);

        var agent = new Agent
        {
            Id = Guid.NewGuid(), SiteId = site.Id, Name = name, Hostname = claim.Hostname, Version = claim.AgentVersion,
            Status = AgentStatus.PendingEnrollment, HeartbeatIntervalSec = Math.Clamp(input.HeartbeatIntervalSec, 10, 300),
        };
        db.Agents.Add(agent);
        var interfaces = JsonSerializer.Deserialize<List<InterfaceInfo>>(claim.InterfacesJson, NmsJson.Options) ?? [];
        AgentHub.UpsertInterfaces(db, agent, interfaces);

        claim.State = ClaimState.Approved;
        claim.AgentId = agent.Id;
        claim.ApprovedBy = approvedBy;
        claim.ApprovedUtc = now;
        // Give the agent time to poll even if approval came at the last second.
        claim.ExpiresUtc = now.AddMinutes(ClaimAuth.CodeLifetimeMinutes);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Claim {Code} approved by {User}: agent {Name} at {Site}", ClaimCode.Format(claim.Code), approvedBy, name, site.Name);
        await NotifyAsync();
        agent.Site = site;
        return agent;
    }

    public async Task<bool> RejectAsync(Guid claimId, CancellationToken ct)
    {
        var claim = await db.AgentClaims.FirstOrDefaultAsync(c => c.Id == claimId && c.State == ClaimState.Pending, ct);
        if (claim is null) return false;
        claim.State = ClaimState.Rejected;
        await db.SaveChangesAsync(ct);
        await NotifyAsync();
        return true;
    }

    /// <summary>Approved claims whose agent never collected within the lifetime are cleaned up with their orphan Agent row.</summary>
    private async Task PruneAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        var stale = await db.AgentClaims.Where(c => c.ExpiresUtc < cutoff).ToListAsync(ct);
        if (stale.Count == 0) return;
        var orphanAgentIds = stale.Where(c => c.State == ClaimState.Approved && c.AgentId != null).Select(c => c.AgentId!.Value).ToList();
        if (orphanAgentIds.Count > 0)
        {
            var orphans = await db.Agents.Where(a => orphanAgentIds.Contains(a.Id) && a.SecretHash == null && a.Status == AgentStatus.PendingEnrollment).ToListAsync(ct);
            db.Agents.RemoveRange(orphans);
        }
        db.AgentClaims.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
    }

    private Task NotifyAsync() => portal.Clients.Group(PortalHub.AllGroup).PendingClaimsChanged();

    private static PendingClaimDto ToDto(AgentClaim c) => new(
        c.Id, ClaimCode.Format(c.Code), c.Hostname, c.AgentVersion, c.RequestedName,
        JsonSerializer.Deserialize<List<InterfaceInfo>>(c.InterfacesJson, NmsJson.Options) ?? [],
        c.RemoteAddress, c.CreatedUtc, c.ExpiresUtc);

    private static bool FixedTimeEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));
}

public sealed class ClaimException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}
