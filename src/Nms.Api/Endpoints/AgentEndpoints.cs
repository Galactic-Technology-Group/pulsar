using Microsoft.EntityFrameworkCore;
using Nms.Api.Auth;
using Nms.Api.Hubs;
using Nms.Api.Services;
using Nms.Data;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Api.Endpoints;

/// <summary>Endpoints called by agents.</summary>
public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("Agent");

        g.MapPost("/agents/enroll", async (EnrollRequest req, NmsDbContext db, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("Enroll");
            var hash = SecretHasher.Sha256(req.EnrollmentToken);
            var agent = await db.Agents.Include(a => a.Site).Include(a => a.Interfaces)
                .FirstOrDefaultAsync(a => a.EnrollmentTokenHash == hash, ct);
            if (agent is null || agent.EnrollmentTokenExpiresUtc < DateTimeOffset.UtcNow || agent.Status == AgentStatus.Revoked)
            {
                log.LogWarning("Enrollment rejected for host {Host}", req.Hostname);
                return Results.Unauthorized();
            }

            var secret = SecretHasher.NewToken(32);
            agent.SecretHash = SecretHasher.HashSecret(secret);
            agent.EnrollmentTokenHash = null;
            agent.EnrollmentTokenExpiresUtc = null;
            agent.Hostname = req.Hostname;
            agent.Version = req.AgentVersion;
            agent.Status = AgentStatus.Offline;
            agent.LastSeenUtc = DateTimeOffset.UtcNow;
            AgentHub.UpsertInterfaces(agent, req.Interfaces);
            await db.SaveChangesAsync(ct);
            log.LogInformation("Agent {AgentId} ({Host}) enrolled at site {Site}", agent.Id, req.Hostname, agent.Site.Name);
            return Results.Ok(new EnrollResponse(agent.Id, secret, agent.SiteId, agent.Site.Name));
        }).AllowAnonymous();

        g.MapPost("/results", async (ResultsBatch batch, HttpContext http, ResultIngestService ingest, CancellationToken ct) =>
        {
            var agentId = http.User.AgentId();
            if (batch.AgentId != agentId) return Results.Forbid();
            if (batch.Results.Count > 5000) return Results.BadRequest("Batch too large (max 5000)");
            var ack = await ingest.IngestAsync(agentId, http.User.SiteId(), batch.Results, ct);
            return Results.Ok(ack);
        }).RequireAuthorization(AgentAuth.Policy).RequireRateLimiting("agent");

        g.MapGet("/agents/version", async (NmsDbContext db, CancellationToken ct) =>
        {
            var r = await db.AgentReleases.AsNoTracking().FirstOrDefaultAsync(x => x.IsCurrent, ct);
            return r is null ? Results.NoContent()
                : Results.Ok(new AgentVersionInfo(r.Version, r.DownloadUrlX64, r.DownloadUrlArm64, r.Sha256X64, r.Sha256Arm64));
        }).RequireAuthorization(AgentAuth.Policy);

        return app;
    }
}
