using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;
using Nms.Worker;
using Nms.Worker.Services;

namespace Nms.Worker.Tests;

public class GatewayProbeServiceTests
{
    private sealed class FakePinger : IGatewayPinger
    {
        public GatewayProbeResult Next { get; set; } = new(true, 12, null);
        public List<(string address, int? port)> Calls { get; } = new();
        public Task<GatewayProbeResult> ProbeAsync(string address, int? port, int timeoutMs, CancellationToken ct)
        {
            Calls.Add((address, port));
            return Task.FromResult(Next);
        }
    }

    private static (ServiceProvider sp, Guid siteId, Guid checkId) Setup(string address = "203.0.113.1")
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();   // hoisted: the options lambda runs once per scope
        services.AddDbContext<NmsDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
        var site = new Site { Id = Guid.NewGuid(), Name = "Branch" };
        var gw = GatewayCheck.Create(site.Id);
        gw.Address = address;
        db.Sites.Add(site); db.Devices.Add(gw); db.SaveChanges();
        return (sp, site.Id, gw.Checks.Single().Id);
    }

    private static GatewayProbeService Service(ServiceProvider sp, FakePinger pinger) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), pinger, Options.Create(new WorkerOptions()), NullLogger<GatewayProbeService>.Instance);

    private static async Task Rewind(ServiceProvider sp, Guid checkId)
    {
        // Make the check due again without waiting for its interval.
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
        var c = await db.Checks.SingleAsync(x => x.Id == checkId);
        c.LastResultUtc = c.LastResultUtc?.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Three_failed_probes_open_a_GatewayDown_incident_and_a_success_closes_it()
    {
        var (sp, _, checkId) = Setup();
        var pinger = new FakePinger { Next = new(false, null, "TimedOut") };
        var svc = Service(sp, pinger);

        for (var i = 0; i < 3; i++) { await svc.RunOnceAsync(CancellationToken.None); await Rewind(sp, checkId); }

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
            var inc = await db.Incidents.SingleAsync();
            Assert.Equal(IncidentKind.GatewayDown, inc.Kind);
            Assert.Equal("Gateway 203.0.113.1 at Branch is DOWN from Azure", inc.Summary);
            Assert.All(pinger.Calls, c => Assert.Equal(GatewayCheck.DefaultPort, c.port));
            Assert.False(inc.Notified);
            Assert.Equal(CheckState.Down, (await db.Checks.SingleAsync()).State);
            Assert.Equal(3, await db.CheckResults.CountAsync());
            Assert.All(await db.CheckResults.ToListAsync(), r => Assert.Equal(GatewayCheck.AzureProbeId, r.AgentId));
        }

        pinger.Next = new(true, 20, null);
        await svc.RunOnceAsync(CancellationToken.None);

        using var verify = sp.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<NmsDbContext>();
        Assert.NotNull((await vdb.Incidents.SingleAsync()).ClosedUtc);
        Assert.Equal(CheckState.Up, (await vdb.Checks.SingleAsync()).State);
    }

    [Fact]
    public async Task A_check_is_not_probed_again_before_its_interval()
    {
        var (sp, _, _) = Setup();
        var pinger = new FakePinger();
        var svc = Service(sp, pinger);
        await svc.RunOnceAsync(CancellationToken.None);
        await svc.RunOnceAsync(CancellationToken.None);
        Assert.Single(pinger.Calls);
    }

    [Fact]
    public async Task Unavailable_probe_leaves_the_check_unknown_and_records_nothing()
    {
        var (sp, _, _) = Setup();
        var pinger = new FakePinger { Next = new(null, null, "IcmpUnavailable") };
        await Service(sp, pinger).RunOnceAsync(CancellationToken.None);
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
        Assert.Equal(CheckState.Unknown, (await db.Checks.SingleAsync()).State);
        Assert.Empty(db.CheckResults);
        Assert.Empty(db.Incidents);
    }

    [Fact]
    public async Task Gateway_without_a_port_is_skipped_unless_icmp_is_enabled()
    {
        var (sp, _, checkId) = Setup();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
            (await db.Checks.SingleAsync(c => c.Id == checkId)).Port = null;
            await db.SaveChangesAsync();
        }
        var pinger = new FakePinger();
        await Service(sp, pinger).RunOnceAsync(CancellationToken.None);
        Assert.Empty(pinger.Calls);

        var icmp = new GatewayProbeService(sp.GetRequiredService<IServiceScopeFactory>(), pinger,
            Options.Create(new WorkerOptions { GatewayIcmpEnabled = true }), NullLogger<GatewayProbeService>.Instance);
        await icmp.RunOnceAsync(CancellationToken.None);
        Assert.Single(pinger.Calls);
        Assert.Null(pinger.Calls[0].port);
    }

    [Fact]
    public async Task Gateway_without_an_address_is_skipped()
    {
        var (sp, _, _) = Setup(address: "");
        var pinger = new FakePinger();
        await Service(sp, pinger).RunOnceAsync(CancellationToken.None);
        Assert.Empty(pinger.Calls);
    }

    [Fact]
    public void Recovery_text_rewrites_each_kind()
    {
        var offline = new Incident { Kind = IncidentKind.AgentOffline, Summary = "Agent pi is OFFLINE; gateway 1.2.3.4 is also unreachable from Azure: site WAN is down" };
        var verdict = OfflineCrossCheck.Classify([(DateTimeOffset.UtcNow, false), (DateTimeOffset.UtcNow, false)]);
        Assert.StartsWith("Agent pi is back online; gateway was unreachable from Azure", NotificationDispatcher.RecoveryText(offline, verdict));
        Assert.Equal("Agent pi is back online", NotificationDispatcher.RecoveryText(offline, null));

        var gw = new Incident { Kind = IncidentKind.GatewayDown, Summary = "Gateway 1.2.3.4 at Branch is DOWN from Azure" };
        Assert.Equal("Gateway 1.2.3.4 at Branch is reachable again from Azure", NotificationDispatcher.RecoveryText(gw, null));

        var dev = new Incident { Kind = IncidentKind.CheckDown, Summary = "core-sw (10.0.0.1) Icmp is DOWN" };
        Assert.Equal("core-sw (10.0.0.1) Icmp recovered", NotificationDispatcher.RecoveryText(dev, null));
    }
}
