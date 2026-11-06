using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nms.Api.Auth;
using Nms.Api.Hubs;
using Nms.Api.Services;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;
using Nms.Shared.Contracts;
using Nms.Shared.Hubs;

namespace Nms.Api.Tests;

public class ClaimServiceTests
{
    private static NmsDbContext Db() => new(new DbContextOptionsBuilder<NmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static ClaimService Svc(NmsDbContext db, CountingHub? hub = null) => new(db, hub ?? new CountingHub(), NullLogger<ClaimService>.Instance);
    private const string Key = "0123456789abcdef0123456789abcdef0123456789ab";
    private static ClaimRequest Req(string host = "pi-lobby") =>
        new(Key, host, "0.3.0", [new InterfaceInfo("eth0", null, "10.0.0.5", 24), new InterfaceInfo("eth0.20", 20, "10.20.0.5", 24)], null);

    private static async Task<Site> Site(NmsDbContext db, string name = "HQ")
    {
        var s = new Site { Id = Guid.NewGuid(), Name = name };
        db.Sites.Add(s); await db.SaveChangesAsync();
        return s;
    }

    [Fact]
    public async Task Create_returns_formatted_code_and_stores_only_the_key_hash()
    {
        var db = Db();
        var hub = new CountingHub();
        var created = await Svc(db, hub).CreateAsync(Req(), "203.0.113.9", default);

        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", created.Code);
        Assert.Equal(ClaimAuth.PollIntervalSec, created.PollIntervalSec);
        var row = await db.AgentClaims.SingleAsync();
        Assert.Equal(ClaimCode.Normalize(created.Code), row.Code);
        Assert.Equal(SecretHasher.Sha256(Key), row.ClaimKeyHash);
        Assert.DoesNotContain(Key, row.ClaimKeyHash);
        Assert.Equal(ClaimState.Pending, row.State);
        Assert.Equal("203.0.113.9", row.RemoteAddress);
        Assert.Equal(1, hub.Notifications);
    }

    [Fact]
    public async Task Create_rejects_short_keys_and_missing_hostname()
    {
        var db = Db();
        var ex = await Assert.ThrowsAsync<ClaimException>(() => Svc(db).CreateAsync(Req() with { ClaimKey = "short" }, null, default));
        Assert.Equal(StatusCodes.Status400BadRequest, ex.Status);
        await Assert.ThrowsAsync<ClaimException>(() => Svc(db).CreateAsync(Req(host: " "), null, default));
    }

    [Fact]
    public async Task Poll_is_pending_until_approved_then_hands_out_credentials_once()
    {
        var db = Db();
        var svc = Svc(db);
        var site = await Site(db);
        var created = await svc.CreateAsync(Req(), null, default);

        var p1 = await svc.PollAsync(created.ClaimId, Key, default);
        Assert.Equal(ClaimService.PollOutcome.Pending, p1.Outcome);
        Assert.Null(p1.Credentials);

        var agent = await svc.ApproveAsync(created.ClaimId, new ClaimApproveInput(site.Id, "Lobby Pi"), "ops@example.com", default);
        Assert.Equal(AgentStatus.PendingEnrollment, agent.Status);
        Assert.Null(agent.SecretHash);
        Assert.Equal(2, await db.AgentInterfaces.CountAsync(i => i.AgentId == agent.Id));

        var p2 = await svc.PollAsync(created.ClaimId, Key, default);
        Assert.Equal(ClaimService.PollOutcome.Approved, p2.Outcome);
        Assert.Equal(agent.Id, p2.Credentials!.AgentId);
        Assert.Equal(site.Id, p2.Credentials.SiteId);
        Assert.Equal("HQ", p2.Credentials.SiteName);
        Assert.True(SecretHasher.Verify(p2.Credentials.Secret, (await db.Agents.SingleAsync()).SecretHash!));
        Assert.Equal(AgentStatus.Offline, (await db.Agents.SingleAsync()).Status);

        // A second collection must fail: the claim is spent.
        var p3 = await svc.PollAsync(created.ClaimId, Key, default);
        Assert.Equal(ClaimService.PollOutcome.Gone, p3.Outcome);
        Assert.Equal(ClaimState.Collected, (await db.AgentClaims.SingleAsync()).State);
    }

    [Fact]
    public async Task Poll_with_wrong_key_or_unknown_id_is_not_found()
    {
        var db = Db();
        var svc = Svc(db);
        var created = await svc.CreateAsync(Req(), null, default);
        Assert.Equal(ClaimService.PollOutcome.NotFound, (await svc.PollAsync(created.ClaimId, "wrong-key-wrong-key-wrong-key-wrong-key", default)).Outcome);
        Assert.Equal(ClaimService.PollOutcome.NotFound, (await svc.PollAsync(created.ClaimId, "", default)).Outcome);
        Assert.Equal(ClaimService.PollOutcome.NotFound, (await svc.PollAsync(Guid.NewGuid(), Key, default)).Outcome);
    }

    [Fact]
    public async Task Expired_claim_is_gone_on_poll_and_cannot_be_approved()
    {
        var db = Db();
        var svc = Svc(db);
        var site = await Site(db);
        var created = await svc.CreateAsync(Req(), null, default);
        var row = await db.AgentClaims.SingleAsync();
        row.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ClaimException>(() => svc.ApproveAsync(created.ClaimId, new ClaimApproveInput(site.Id, "x"), "ops", default));
        Assert.Equal(StatusCodes.Status410Gone, ex.Status);
        Assert.Equal(ClaimService.PollOutcome.Gone, (await svc.PollAsync(created.ClaimId, Key, default)).Outcome);
        Assert.Empty(await svc.ListPendingAsync(default));
    }

    [Fact]
    public async Task Rejected_claim_is_gone_and_leaves_no_agent()
    {
        var db = Db();
        var svc = Svc(db);
        var created = await svc.CreateAsync(Req(), null, default);
        Assert.True(await svc.RejectAsync(created.ClaimId, default));
        Assert.False(await svc.RejectAsync(created.ClaimId, default));
        Assert.Equal(ClaimService.PollOutcome.Gone, (await svc.PollAsync(created.ClaimId, Key, default)).Outcome);
        Assert.Empty(db.Agents);
    }

    [Fact]
    public async Task Approve_refuses_duplicate_agent_name_at_the_site_and_unknown_site()
    {
        var db = Db();
        var svc = Svc(db);
        var site = await Site(db);
        db.Agents.Add(new Agent { Id = Guid.NewGuid(), SiteId = site.Id, Name = "pi-lobby" });
        await db.SaveChangesAsync();
        var created = await svc.CreateAsync(Req(), null, default);

        var dup = await Assert.ThrowsAsync<ClaimException>(() => svc.ApproveAsync(created.ClaimId, new ClaimApproveInput(site.Id, "pi-lobby"), "ops", default));
        Assert.Equal(StatusCodes.Status409Conflict, dup.Status);
        var nosite = await Assert.ThrowsAsync<ClaimException>(() => svc.ApproveAsync(created.ClaimId, new ClaimApproveInput(Guid.NewGuid(), "other"), "ops", default));
        Assert.Equal(StatusCodes.Status404NotFound, nosite.Status);
        Assert.Equal(ClaimState.Pending, (await db.AgentClaims.SingleAsync()).State);
    }

    [Fact]
    public async Task Approve_uses_hostname_when_no_name_given_and_records_who_approved()
    {
        var db = Db();
        var svc = Svc(db);
        var site = await Site(db);
        var created = await svc.CreateAsync(Req("edge-01"), null, default);
        var agent = await svc.ApproveAsync(created.ClaimId, new ClaimApproveInput(site.Id, "  "), "ops@example.com", default);
        Assert.Equal("edge-01", agent.Name);
        Assert.Equal("edge-01", agent.Hostname);
        var row = await db.AgentClaims.SingleAsync();
        Assert.Equal("ops@example.com", row.ApprovedBy);
        Assert.Equal(agent.Id, row.AgentId);
    }

    [Fact]
    public async Task ListPending_only_shows_live_pending_claims()
    {
        var db = Db();
        var svc = Svc(db);
        var a = await svc.CreateAsync(Req("a"), null, default);
        var b = await svc.CreateAsync(Req("b") with { ClaimKey = Key + "b" }, null, default);
        await svc.RejectAsync(b.ClaimId, default);
        var list = await svc.ListPendingAsync(default);
        var only = Assert.Single(list);
        Assert.Equal(a.ClaimId, only.Id);
        Assert.Equal(a.Code, only.Code);
        Assert.Equal(2, only.Interfaces.Count);
    }

    [Fact]
    public async Task Prune_removes_day_old_claims_and_their_uncollected_agents()
    {
        var db = Db();
        var svc = Svc(db);
        var site = await Site(db);
        var old = await svc.CreateAsync(Req("old"), null, default);
        var agent = await svc.ApproveAsync(old.ClaimId, new ClaimApproveInput(site.Id, "old"), "ops", default);
        var row = await db.AgentClaims.SingleAsync();
        row.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-2);
        await db.SaveChangesAsync();

        await svc.CreateAsync(Req("new") with { ClaimKey = Key + "n" }, null, default);   // triggers prune

        Assert.Single(db.AgentClaims);
        Assert.False(await db.Agents.AnyAsync(a => a.Id == agent.Id));
    }

    /// <summary>IHubContext stand-in that counts PendingClaimsChanged broadcasts.</summary>
    private sealed class CountingHub : IHubContext<PortalHub, IPortalClient>
    {
        public int Notifications;
        public IHubClients<IPortalClient> Clients => new C(this);
        public IGroupManager Groups { get; } = new G();

        private sealed class C(CountingHub owner) : IHubClients<IPortalClient>
        {
            private IPortalClient Client() => new P(owner);
            public IPortalClient All => Client();
            public IPortalClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => Client();
            public IPortalClient Client(string connectionId) => Client();
            public IPortalClient Clients(IReadOnlyList<string> connectionIds) => Client();
            public IPortalClient Group(string groupName) => Client();
            public IPortalClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Client();
            public IPortalClient Groups(IReadOnlyList<string> groupNames) => Client();
            public IPortalClient User(string userId) => Client();
            public IPortalClient Users(IReadOnlyList<string> userIds) => Client();
        }
        private sealed class P(CountingHub owner) : IPortalClient
        {
            public Task CheckStateChanged(Guid siteId, Guid deviceId, Guid checkId, CheckState state, DateTimeOffset at) => Task.CompletedTask;
            public Task AgentStatusChanged(Guid siteId, Guid agentId, AgentStatus status, DateTimeOffset at) => Task.CompletedTask;
            public Task IncidentChanged(Guid incidentId, Guid siteId, bool open) => Task.CompletedTask;
            public Task PendingClaimsChanged() { owner.Notifications++; return Task.CompletedTask; }
        }
        private sealed class G : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
        }
    }
}

public class ClaimCodeTests
{
    [Fact]
    public void Generated_codes_use_the_unambiguous_alphabet_and_vary()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => ClaimCode.Generate()).ToList();
        Assert.All(codes, c => { Assert.Equal(8, c.Length); Assert.True(ClaimCode.IsValid(c)); });
        Assert.DoesNotContain(codes, c => c.IndexOfAny(['0', '1', 'I', 'L', 'O']) >= 0);
        Assert.True(codes.Distinct().Count() > 190);
    }

    [Theory]
    [InlineData("kd7p-4qrs", "KD7P4QRS")]
    [InlineData(" KD7P 4QRS ", "KD7P4QRS")]
    [InlineData("KD7P4QRS", "KD7P4QRS")]
    public void Normalize_strips_separators_and_case(string input, string expected) => Assert.Equal(expected, ClaimCode.Normalize(input));

    [Fact]
    public void Format_inserts_the_dash() => Assert.Equal("KD7P-4QRS", ClaimCode.Format("KD7P4QRS"));
}
