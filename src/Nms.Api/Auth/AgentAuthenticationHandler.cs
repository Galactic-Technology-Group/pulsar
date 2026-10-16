using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nms.Data;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Api.Auth;

/// <summary>
/// Authenticates agents by X-Nms-Agent-Id + X-Nms-Agent-Secret. Verified secrets are cached for 5 minutes
/// so a heartbeat every 30 s does not cost a PBKDF2 round each time.
/// </summary>
public sealed class AgentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    NmsDbContext db,
    IMemoryCache cache) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string ClaimAgentId = "nms:agent_id";
    public const string ClaimSiteId = "nms:site_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(AgentAuth.AgentIdHeader, out var idHeader) ||
            !Request.Headers.TryGetValue(AgentAuth.AgentSecretHeader, out var secretHeader))
            return AuthenticateResult.NoResult();

        if (!Guid.TryParse(idHeader, out var agentId) || string.IsNullOrEmpty(secretHeader)) return AuthenticateResult.Fail("Malformed agent credentials");
        var secret = secretHeader.ToString();

        var cacheKey = $"agentauth:{agentId}:{SecretHasher.Sha256(secret)}";
        if (!cache.TryGetValue(cacheKey, out Guid siteId))
        {
            var agent = await db.Agents.AsNoTracking()
                .Where(a => a.Id == agentId)
                .Select(a => new { a.SecretHash, a.Status, a.SiteId })
                .FirstOrDefaultAsync(Context.RequestAborted);

            if (agent?.SecretHash is null || agent.Status == AgentStatus.Revoked || !SecretHasher.Verify(secret, agent.SecretHash))
                return AuthenticateResult.Fail("Invalid agent credentials");

            siteId = agent.SiteId;
            cache.Set(cacheKey, siteId, TimeSpan.FromMinutes(5));
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, agentId.ToString()),
            new Claim(ClaimAgentId, agentId.ToString()),
            new Claim(ClaimSiteId, siteId.ToString()),
            new Claim(ClaimTypes.Role, "Agent"),
        }, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

public static class AgentClaims
{
    public static Guid AgentId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(AgentAuthenticationHandler.ClaimAgentId)!);
    public static Guid SiteId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(AgentAuthenticationHandler.ClaimSiteId)!);
}
