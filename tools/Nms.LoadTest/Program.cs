// Pulsar load test: simulated site agents against a real API. See docs/load-test.md.
//
//   Nms.LoadTest run   --api <url> [--agents 50] [--checks 300] [--minutes 30]
//                      [--replay-agents 10] [--replay-at 10] [--replay-gap 10] [--keep]
//   Nms.LoadTest clean
//
// Needs ConnectionStrings__Nms (seed and clean write the database directly). Everything it creates is named
// "loadtest-*" and removed at the end, on SIGTERM, or by "clean".
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Nms.Data;
using Nms.LoadTest;

var cmd = args.FirstOrDefault() ?? "run";
string Arg(string name, string def) { var i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
bool Flag(string name) => args.Contains("--" + name);

var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Nms")
    ?? throw new InvalidOperationException("ConnectionStrings__Nms is not set");
NmsDbContext NewDb() => new(new DbContextOptionsBuilder<NmsDbContext>()
    .UseNpgsql(cs, n => n.MigrationsHistoryTable("__ef_migrations", "nms").CommandTimeout(300)).Options);

if (cmd == "clean")
{
    await Seeder.CleanAsync(NewDb, Console.Out);
    return 0;
}
if (cmd != "run") { Console.Error.WriteLine($"Unknown command {cmd}; use run or clean"); return 2; }

var opts = new RunOptions(
    Api: (Arg("api", "") is { Length: > 0 } api ? api : Environment.GetEnvironmentVariable("NMS_API") ?? throw new InvalidOperationException("--api or NMS_API required")).TrimEnd('/'),
    Agents: int.Parse(Arg("agents", "50")),
    Checks: int.Parse(Arg("checks", "300")),
    Minutes: double.Parse(Arg("minutes", "30"), System.Globalization.CultureInfo.InvariantCulture),
    ReplayAgents: int.Parse(Arg("replay-agents", "10")),
    ReplayAtMinute: double.Parse(Arg("replay-at", "10"), System.Globalization.CultureInfo.InvariantCulture),
    ReplayGapMinutes: double.Parse(Arg("replay-gap", "10"), System.Globalization.CultureInfo.InvariantCulture));

using var stop = new CancellationTokenSource();
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Console.WriteLine("SIGTERM: stopping and cleaning up"); stop.Cancel(); });
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

Console.WriteLine($"Load test: {opts.Agents} agents x {opts.Checks} checks for {opts.Minutes} min against {opts.Api}; " +
                  $"{opts.ReplayAgents} agents drop out at minute {opts.ReplayAtMinute} for {opts.ReplayGapMinutes} min and replay");
try
{
    await Seeder.CleanAsync(NewDb, Console.Out);   // leftovers from an interrupted run
    // Report the current release's version, as real agents do; anything else makes the API push UpdateAvailable on every heartbeat.
    string version;
    await using (var db = NewDb())
        version = await db.AgentReleases.Where(r => r.IsCurrent).Select(r => r.Version).FirstOrDefaultAsync() ?? "0.0.0";
    var seeded = await Seeder.SeedAsync(NewDb, opts, version, Console.Out);
    var stats = new Stats();
    using var reporter = new Reporter(stats, Console.Out);

    var started = DateTimeOffset.UtcNow;
    var sims = seeded.Select((a, i) => new SimAgent(a, opts, stats, i < opts.ReplayAgents, started, version)).ToList();
    using var runFor = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
    runFor.CancelAfter(TimeSpan.FromMinutes(opts.Minutes));
    // Stagger connects over the first minute like agents restarting after an API deploy, not a thundering herd.
    var tasks = sims.Select((s, i) => s.RunAsync(TimeSpan.FromMilliseconds(60_000.0 * i / sims.Count), runFor.Token)).ToList();
    await Task.WhenAll(tasks);
    reporter.Final(started, opts);
}
finally
{
    if (Flag("keep")) Console.WriteLine("--keep: leaving loadtest-* data in place; run 'clean' to remove it");
    else await Seeder.CleanAsync(NewDb, Console.Out);
}
return 0;

namespace Nms.LoadTest
{
    public sealed record RunOptions(string Api, int Agents, int Checks, double Minutes, int ReplayAgents, double ReplayAtMinute, double ReplayGapMinutes);
}
