using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;

namespace Nms.LoadTest;

public sealed record SeededAgent(Guid AgentId, Guid SiteId, string Name, string Secret);

/// <summary>Creates and removes the loadtest-* sites, agents, devices and checks directly in the database.</summary>
public static class Seeder
{
    public const string Prefix = "loadtest-";

    public static async Task<List<SeededAgent>> SeedAsync(Func<NmsDbContext> newDb, RunOptions o, string version, TextWriter log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new List<SeededAgent>();
        var maintenanceEnd = DateTimeOffset.UtcNow.AddMinutes(o.Minutes + 60);
        for (var i = 1; i <= o.Agents; i++)
        {
            await using var db = newDb();
            var site = new Site { Id = Guid.NewGuid(), Name = $"{Prefix}{i:D2}" };
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var agent = new Agent
            {
                Id = Guid.NewGuid(), SiteId = site.Id, Name = $"{Prefix}agent-{i:D2}", Hostname = $"{Prefix}host-{i:D2}",
                SecretHash = HashSecret(secret), Status = AgentStatus.Online, Version = version, LastSeenUtc = DateTimeOffset.UtcNow,
                // The worker's watchdog marks an agent offline after 3 missed intervals. A day keeps it from ever paging
                // for a simulated agent (also if this run dies), while the simulator still heartbeats every 30 s.
                HeartbeatIntervalSec = 86_400,
            };
            db.Sites.Add(site);
            db.Agents.Add(agent);
            // Incidents are recorded but marked notified, so nothing reaches the real email/Teams channels.
            db.MaintenanceWindows.Add(new MaintenanceWindow
            {
                Id = Guid.NewGuid(), SiteId = site.Id, StartUtc = DateTimeOffset.UtcNow.AddMinutes(-1), EndUtc = maintenanceEnd, Reason = "load test",
            });
            for (var d = 0; d < o.Checks; d++)
            {
                var tcp = d % 5 == 4;   // 80 % ICMP, 20 % TCP: the server-side cost is the same, the summaries differ
                db.Devices.Add(new Device
                {
                    Id = Guid.NewGuid(), SiteId = site.Id, Name = $"lt-{i:D2}-dev-{d:D3}", Address = $"10.{i}.{d / 250}.{d % 250 + 1}",
                    IsCritical = true, Tags = ["loadtest"],
                    Checks = { new Check { Id = Guid.NewGuid(), Type = tcp ? CheckType.TcpPort : CheckType.Icmp, Port = tcp ? 443 : null, IntervalSec = 60, TimeoutMs = 2000, FailureThreshold = 3 } },
                });
            }
            await db.SaveChangesAsync();
            result.Add(new SeededAgent(agent.Id, site.Id, agent.Name, secret));
        }
        log.WriteLine($"Seeded {o.Agents} sites x {o.Checks} checks in {sw.Elapsed.TotalSeconds:0.0} s");
        return result;
    }

    public static async Task CleanAsync(Func<NmsDbContext> newDb, TextWriter log)
    {
        await using var db = newDb();
        var siteIds = await db.Sites.Where(s => s.Name.StartsWith(Prefix)).Select(s => s.Id).ToListAsync();
        if (siteIds.Count == 0) { log.WriteLine("Clean: nothing to remove"); return; }
        var agentIds = await db.Agents.Where(a => siteIds.Contains(a.SiteId)).Select(a => a.Id).ToListAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // check_results is a hypertable without foreign keys, so its rows go explicitly; the rest cascades from sites.
        var results = await db.CheckResults.Where(r => agentIds.Contains(r.AgentId)).ExecuteDeleteAsync();
        var incidents = await db.Incidents.Where(i => siteIds.Contains(i.SiteId)).ExecuteDeleteAsync();
        await db.MaintenanceWindows.Where(m => m.SiteId != null && siteIds.Contains(m.SiteId.Value)).ExecuteDeleteAsync();
        var sites = await db.Sites.Where(s => siteIds.Contains(s.Id)).ExecuteDeleteAsync();
        log.WriteLine($"Clean: removed {sites} sites, {agentIds.Count} agents, {incidents} incidents, {results} results in {sw.Elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>Same format as Nms.Api.Auth.SecretHasher (pbkdf2$iterations$salt$hash).</summary>
    private static string HashSecret(string secret)
    {
        const int iterations = 100_000;
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
}
