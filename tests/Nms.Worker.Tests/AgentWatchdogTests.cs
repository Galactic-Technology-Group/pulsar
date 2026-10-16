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

public class AgentWatchdogTests
{
    private static (ServiceProvider sp, Guid agentId) Setup(DateTimeOffset? lastSeen, AgentStatus status = AgentStatus.Online)
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<NmsDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
        var site = new Site { Id = Guid.NewGuid(), Name = "A" };
        var agent = new Agent { Id = Guid.NewGuid(), SiteId = site.Id, Name = "pi", Status = status, HeartbeatIntervalSec = 30, LastSeenUtc = lastSeen };
        db.Sites.Add(site); db.Agents.Add(agent); db.SaveChanges();
        return (sp, agent.Id);
    }

    private static AgentWatchdog Watchdog(ServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(new WorkerOptions { OfflineAfterHeartbeats = 3 }), NullLogger<AgentWatchdog>.Instance);

    [Fact]
    public async Task Stale_heartbeat_marks_offline_and_opens_one_incident()
    {
        var (sp, agentId) = Setup(DateTimeOffset.UtcNow.AddSeconds(-120));
        var wd = Watchdog(sp);

        await wd.RunOnceAsync(CancellationToken.None);
        await wd.RunOnceAsync(CancellationToken.None); // second pass must not duplicate

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
        Assert.Equal(AgentStatus.Offline, (await db.Agents.SingleAsync()).Status);
        var incidents = await db.Incidents.Where(i => i.Kind == IncidentKind.AgentOffline && i.AgentId == agentId).ToListAsync();
        Assert.Single(incidents);
        Assert.Null(incidents[0].ClosedUtc);
    }

    [Fact]
    public async Task Fresh_heartbeat_keeps_online_and_closes_open_incident()
    {
        var (sp, agentId) = Setup(DateTimeOffset.UtcNow.AddSeconds(-10));
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
            var siteId = (await db.Agents.SingleAsync()).SiteId;
            db.Incidents.Add(new Incident { Id = Guid.NewGuid(), Kind = IncidentKind.AgentOffline, AgentId = agentId, SiteId = siteId, OpenedUtc = DateTimeOffset.UtcNow.AddMinutes(-5) });
            await db.SaveChangesAsync();
        }

        await Watchdog(sp).RunOnceAsync(CancellationToken.None);

        using var verify = sp.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<NmsDbContext>();
        Assert.Equal(AgentStatus.Online, (await vdb.Agents.SingleAsync()).Status);
        Assert.NotNull((await vdb.Incidents.SingleAsync()).ClosedUtc);
    }

    [Fact]
    public async Task Within_three_intervals_is_not_stale()
    {
        var (sp, _) = Setup(DateTimeOffset.UtcNow.AddSeconds(-80)); // 30 s * 3 = 90 s window
        await Watchdog(sp).RunOnceAsync(CancellationToken.None);
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
        Assert.Equal(AgentStatus.Online, (await db.Agents.SingleAsync()).Status);
        Assert.Empty(db.Incidents);
    }
}
