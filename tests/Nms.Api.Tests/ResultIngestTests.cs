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

    [Fact]
    public async Task Suppressed_incident_never_sends_a_recovery_either()
    {
        var (db, siteId, agentId, check) = await Seed(critical: false);
        var svc = Svc(db);
        var t = DateTimeOffset.UtcNow;
        await svc.IngestAsync(agentId, siteId, [R(check.Id, t, false), R(check.Id, t.AddSeconds(1), false), R(check.Id, t.AddSeconds(2), false)], default);
        var inc = await db.Incidents.SingleAsync();
        Assert.True(inc.Notified);
        Assert.True(inc.RecoveryNotified);
    }

    private static async Task<(NmsDbContext db, Guid siteId, Guid agentId, Check check)> SeedUps()
    {
        var (db, siteId, agentId, check) = await Seed();
        check.Type = CheckType.Snmp; check.SnmpTemplate = "ups"; check.FailureThreshold = 1;
        db.MetricThresholds.Add(new MetricThreshold { Id = Guid.NewGuid(), CheckId = check.Id, MetricKey = "upsOutputSource", Op = ThresholdOp.Equals, Value = 5, Label = "UPS on battery" });
        db.MetricThresholds.Add(new MetricThreshold { Id = Guid.NewGuid(), CheckId = check.Id, MetricKey = "upsOutputLoad", Op = ThresholdOp.Above, Value = 80, Label = "UPS load above 80%" });
        await db.SaveChangesAsync();
        return (db, siteId, agentId, check);
    }

    private static CheckResultDto Snmp(Guid checkId, DateTimeOffset t, double source, params (string phase, double load)[] loads) =>
        new(checkId, t, true, 3, null, [new("upsOutputSource", null, source), .. loads.Select(l => new MetricSampleDto("upsOutputLoad", l.phase, l.load))]);

    [Fact]
    public async Task Snmp_readings_are_stored_and_a_crossed_limit_opens_then_clears_an_incident()
    {
        var (db, siteId, agentId, check) = await SeedUps();
        var svc = Svc(db);
        var t = DateTimeOffset.UtcNow.AddMinutes(-10);

        await svc.IngestAsync(agentId, siteId, [Snmp(check.Id, t, 3, ("1", 40))], default);
        Assert.Equal(2, await db.MetricSamples.CountAsync());
        Assert.Empty(db.Incidents);
        Assert.Contains("upsOutputSource", (await db.Checks.SingleAsync()).LastMetricsJson);

        await svc.IngestAsync(agentId, siteId, [Snmp(check.Id, t.AddMinutes(1), 5, ("1", 40))], default);
        var inc = await db.Incidents.SingleAsync();
        Assert.Equal(IncidentKind.Threshold, inc.Kind);
        Assert.Equal("core-sw (10.0.0.1): UPS on battery (Power source is battery)", inc.Summary);
        Assert.False(inc.Notified);

        // Still on battery: no second incident. Back on mains: closed.
        await svc.IngestAsync(agentId, siteId, [Snmp(check.Id, t.AddMinutes(2), 5, ("1", 40))], default);
        Assert.Single(db.Incidents);
        await svc.IngestAsync(agentId, siteId, [Snmp(check.Id, t.AddMinutes(3), 3, ("1", 40))], default);
        Assert.NotNull((await db.Incidents.SingleAsync()).ClosedUtc);
    }

    [Fact]
    public async Task A_limit_on_a_table_reading_is_judged_per_row()
    {
        var (db, siteId, agentId, check) = await SeedUps();
        var svc = Svc(db);
        var t = DateTimeOffset.UtcNow.AddMinutes(-10);

        await svc.IngestAsync(agentId, siteId, [Snmp(check.Id, t, 3, ("1", 85), ("2", 90), ("3", 20))], default);
        var open = await db.Incidents.OrderBy(i => i.MetricInstance).ToListAsync();
        Assert.Equal(["1", "2"], open.Select(i => i.MetricInstance));
        Assert.Contains("Load [2] is 90 %", open[1].Summary);

        await svc.IngestAsync(agentId, siteId, [Snmp(check.Id, t.AddMinutes(1), 3, ("1", 50), ("2", 90), ("3", 20))], default);
        Assert.Equal(["2"], (await db.Incidents.Where(i => i.ClosedUtc == null).ToListAsync()).Select(i => i.MetricInstance));
    }

    [Fact]
    public async Task A_failed_poll_leaves_limits_alone_and_drives_the_check_down()
    {
        var (db, siteId, agentId, check) = await SeedUps();
        var svc = Svc(db);
        var t = DateTimeOffset.UtcNow.AddMinutes(-10);
        await svc.IngestAsync(agentId, siteId, [Snmp(check.Id, t, 5)], default);
        await svc.IngestAsync(agentId, siteId, [new CheckResultDto(check.Id, t.AddMinutes(1), false, null, "TimedOut")], default);

        var incidents = await db.Incidents.ToListAsync();
        Assert.Contains(incidents, i => i.Kind == IncidentKind.Threshold && i.ClosedUtc == null);   // not cleared by silence
        Assert.Contains(incidents, i => i.Kind == IncidentKind.CheckDown);
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
            public Task PendingClaimsChanged() => Task.CompletedTask;
        }
        private sealed class FakeGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
        }
    }
}
