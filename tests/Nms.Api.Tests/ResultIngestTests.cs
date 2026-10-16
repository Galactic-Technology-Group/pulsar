using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nms.Api.Hubs;
using Nms.Api.Services;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;
using Nms.Shared.Contracts;
using Nms.Shared.Hubs;

namespace Nms.Api.Tests;

public class ResultIngestTests
{
    private static NmsDbContext Db() => new(new DbContextOptionsBuilder<NmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(NmsDbContext db, Guid siteId, Guid agentId, Check check)> Seed(bool critical = true)
    {
        var db = Db();
        var site = new Site { Id = Guid.NewGuid(), Name = "A" };
        var agent = new Agent { Id = Guid.NewGuid(), SiteId = site.Id, Name = "pi", Status = AgentStatus.Online };
        var device = new Device { Id = Guid.NewGuid(), SiteId = site.Id, Name = "core-sw", Address = "10.0.0.1", IsCritical = critical };
        var check = new Check { Id = Guid.NewGuid(), DeviceId = device.Id, Device = device, Type = CheckType.Icmp, FailureThreshold = 3 };
        db.AddRange(site, agent, device, check);
        await db.SaveChangesAsync();
        return (db, site.Id, agent.Id, check);
    }

    private static ResultIngestService Svc(NmsDbContext db) => new(db, new FakeHub(), NullLogger<ResultIngestService>.Instance);

    [Fact]
    public async Task Three_failures_open_an_incident_and_recovery_closes_it()
    {
        var (db, siteId, agentId, check) = await Seed();
        var svc = Svc(db);
        var t = DateTimeOffset.UtcNow.AddMinutes(-5);

        await svc.IngestAsync(agentId, siteId, [R(check.Id, t, false), R(check.Id, t.AddSeconds(60), false)], default);
        Assert.Empty(db.Incidents);
        Assert.Equal(CheckState.Degraded, (await db.Checks.SingleAsync()).State);

        await svc.IngestAsync(agentId, siteId, [R(check.Id, t.AddSeconds(120), false)], default);
        var inc = await db.Incidents.SingleAsync();
        Assert.Null(inc.ClosedUtc);
        Assert.False(inc.Notified);
        Assert.Contains("core-sw", inc.Summary);

        await svc.IngestAsync(agentId, siteId, [R(check.Id, t.AddSeconds(180), true)], default);
        Assert.NotNull((await db.Incidents.SingleAsync()).ClosedUtc);
        Assert.Equal(CheckState.Up, (await db.Checks.SingleAsync()).State);
    }

    [Fact]
    public async Task Duplicates_are_counted_not_stored()
    {
        var (db, siteId, agentId, check) = await Seed();
        var svc = Svc(db);
        var t = DateTimeOffset.UtcNow;
        var first = await svc.IngestAsync(agentId, siteId, [R(check.Id, t, true)], default);
        var again = await svc.IngestAsync(agentId, siteId, [R(check.Id, t, true), R(check.Id, t.AddSeconds(1), true)], default);
        Assert.Equal((1, 0), (first.Accepted, first.Duplicates));
        Assert.Equal((1, 1), (again.Accepted, again.Duplicates));
        Assert.Equal(2, await db.CheckResults.CountAsync());
    }

    [Fact]
    public async Task Old_replayed_results_do_not_flap_state()
    {
        var (db, siteId, agentId, check) = await Seed();
        var svc = Svc(db);
        var t = DateTimeOffset.UtcNow;
        await svc.IngestAsync(agentId, siteId, [R(check.Id, t, true)], default);
        await svc.IngestAsync(agentId, siteId, [R(check.Id, t.AddMinutes(-10), false), R(check.Id, t.AddMinutes(-9), false), R(check.Id, t.AddMinutes(-8), false)], default);
        Assert.Equal(CheckState.Up, (await db.Checks.SingleAsync()).State);
        Assert.Empty(db.Incidents);
        Assert.Equal(4, await db.CheckResults.CountAsync());
    }

    [Fact]
    public async Task Non_critical_device_incident_is_pre_marked_notified()
    {
        var (db, siteId, agentId, check) = await Seed(critical: false);
        var svc = Svc(db);
        var t = DateTimeOffset.UtcNow;
        await svc.IngestAsync(agentId, siteId, [R(check.Id, t, false), R(check.Id, t.AddSeconds(1), false), R(check.Id, t.AddSeconds(2), false)], default);
        Assert.True((await db.Incidents.SingleAsync()).Notified);
    }

    [Fact]
    public async Task Results_for_checks_at_another_site_are_dropped()
    {
        var (db, siteId, agentId, _) = await Seed();
        var ack = await Svc(db).IngestAsync(agentId, siteId, [R(Guid.NewGuid(), DateTimeOffset.UtcNow, false)], default);
        Assert.Equal(0, ack.Accepted);
        Assert.Empty(db.CheckResults);
    }

    private static CheckResultDto R(Guid checkId, DateTimeOffset t, bool ok) => new(checkId, t, ok, ok ? 5 : null, ok ? null : "TimedOut");

    /// <summary>IHubContext stand-in that swallows every broadcast.</summary>
    private sealed class FakeHub : IHubContext<PortalHub, IPortalClient>
    {
        public IHubClients<IPortalClient> Clients { get; } = new FakeClients();
        public IGroupManager Groups { get; } = new FakeGroups();

        private sealed class FakeClients : IHubClients<IPortalClient>
        {
            private static readonly IPortalClient Noop = new NoopClient();
            public IPortalClient All => Noop;
            public IPortalClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => Noop;
            public IPortalClient Client(string connectionId) => Noop;
            public IPortalClient Clients(IReadOnlyList<string> connectionIds) => Noop;
            public IPortalClient Group(string groupName) => Noop;
            public IPortalClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Noop;
            public IPortalClient Groups(IReadOnlyList<string> groupNames) => Noop;
            public IPortalClient User(string userId) => Noop;
            public IPortalClient Users(IReadOnlyList<string> userIds) => Noop;
        }
        private sealed class NoopClient : IPortalClient
        {
            public Task CheckStateChanged(Guid siteId, Guid deviceId, Guid checkId, CheckState state, DateTimeOffset at) => Task.CompletedTask;
            public Task AgentStatusChanged(Guid siteId, Guid agentId, AgentStatus status, DateTimeOffset at) => Task.CompletedTask;
            public Task IncidentChanged(Guid incidentId, Guid siteId, bool open) => Task.CompletedTask;
        }
        private sealed class FakeGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
        }
    }
}
