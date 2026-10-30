using Microsoft.EntityFrameworkCore;
using Nms.Api.Hubs;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared.Contracts;

namespace Nms.Api.Tests;

public class AgentInterfaceUpsertTests
{
    private static NmsDbContext Db(string name) => new(new DbContextOptionsBuilder<NmsDbContext>().UseInMemoryDatabase(name).Options);

    [Fact]
    public async Task New_interfaces_are_inserted_and_existing_ones_updated()
    {
        var dbName = Guid.NewGuid().ToString();
        var site = new Site { Id = Guid.NewGuid(), Name = "HQ" };
        var agent = new Agent { Id = Guid.NewGuid(), SiteId = site.Id, Name = "a1" };
        await using (var seed = Db(dbName)) { seed.AddRange(site, agent); await seed.SaveChangesAsync(); }

        await using var db = Db(dbName);
        var tracked = await db.Agents.Include(a => a.Interfaces).SingleAsync(a => a.Id == agent.Id);
        AgentHub.UpsertInterfaces(db, tracked, [new InterfaceInfo("eth0", null, "10.0.0.5", 24)]);
        Assert.All(db.ChangeTracker.Entries<AgentInterface>(), e => Assert.Equal(EntityState.Added, e.State));
        await db.SaveChangesAsync();   // would throw DbUpdateConcurrencyException if tracked as Modified

        AgentHub.UpsertInterfaces(db, tracked, [new InterfaceInfo("eth0", 10, "10.0.0.5", 24), new InterfaceInfo("eth0.20", 20, "10.20.0.5", 24)]);
        await db.SaveChangesAsync();

        var all = await db.AgentInterfaces.Where(i => i.AgentId == agent.Id).OrderBy(i => i.Name).ToListAsync();
        Assert.Equal(["eth0", "eth0.20"], all.Select(i => i.Name));
        Assert.Equal(10, all[0].VlanId);
    }
}
