using System.Collections.Concurrent;
using System.Text.Json;

namespace Nms.LoadTest;

public sealed class Stats
{
    public readonly record struct PostSample(DateTimeOffset At, double Ms, int Status, int Sent, int Accepted, int Duplicates);

    public readonly ConcurrentQueue<PostSample> Posts = new();
    public readonly ConcurrentQueue<(DateTimeOffset At, double Ms)> ConfigFetches = new();
    private readonly ConcurrentQueue<string> _notes = new();

    public int Connected, Connects, ConnectErrors, HubDrops, Heartbeats, HeartbeatErrors, ConfigPushes, UpdatePushes, OutagesStarted, LeftQueued;

    public void Post(double ms, int status, int sent, int accepted, int duplicates) =>
        Posts.Enqueue(new PostSample(DateTimeOffset.UtcNow, ms, status, sent, accepted, duplicates));

    public void ConfigFetch(double ms) => ConfigFetches.Enqueue((DateTimeOffset.UtcNow, ms));

    /// <summary>Free-text events, printed (deduplicated by prefix) with the next minute line.</summary>
    public void Note(string s) { if (_notes.Count < 2000) _notes.Enqueue(s); }

    public List<string> DrainNotes() { var l = new List<string>(); while (_notes.TryDequeue(out var s)) l.Add(s); return l; }

    public static double Pct(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1)];
}

/// <summary>Prints one line per minute while the test runs and a summary per phase at the end.</summary>
public sealed class Reporter : IDisposable
{
    private readonly Stats _s;
    private readonly TextWriter _out;
    private readonly Timer _timer;
    private DateTimeOffset _last = DateTimeOffset.UtcNow;

    public Reporter(Stats s, TextWriter output)
    {
        _s = s; _out = output;
        _timer = new Timer(_ => Minute(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _out.WriteLine("time      posts  results/s  p50ms  p95ms  p99ms  maxms  non2xx  429  neterr  hub  drops  hb");
    }

    private void Minute()
    {
        var now = DateTimeOffset.UtcNow;
        var w = _s.Posts.Where(p => p.At > _last && p.At <= now).ToList();
        var secs = Math.Max(1, (now - _last).TotalSeconds);
        _last = now;
        var ms = w.Where(p => p.Status is >= 200 and < 300).Select(p => p.Ms).Order().ToList();
        _out.WriteLine($"{now:HH:mm:ss}  {w.Count,5}  {w.Sum(p => p.Accepted) / secs,9:0.0}  {Stats.Pct(ms, 50),5:0}  {Stats.Pct(ms, 95),5:0}  {Stats.Pct(ms, 99),5:0}  {(ms.Count == 0 ? 0 : ms[^1]),5:0}" +
                       $"  {w.Count(p => p.Status >= 300),6}  {w.Count(p => p.Status == 429),3}  {w.Count(p => p.Status == 0),6}  {_s.Connected,3}  {_s.HubDrops,5}  {_s.Heartbeats}");
        foreach (var g in _s.DrainNotes().GroupBy(n => n.Split(':')[0].Split(' ').Skip(1).FirstOrDefault() ?? n).Take(8))
            _out.WriteLine($"          note x{g.Count()}: {g.First()}");
        _out.Flush();
    }

    public void Final(DateTimeOffset started, RunOptions o)
    {
        Minute();
        var replayFrom = started + TimeSpan.FromMinutes(o.ReplayAtMinute + o.ReplayGapMinutes);
        var phases = new (string Name, DateTimeOffset From, DateTimeOffset To)[]
        {
            ("warm-up (first 2 min)", started, started.AddMinutes(2)),
            ("steady", started.AddMinutes(2), started.AddMinutes(o.ReplayAtMinute)),
            ("outage (others steady)", started.AddMinutes(o.ReplayAtMinute), replayFrom),
            ("replay (3 min after reconnect)", replayFrom, replayFrom.AddMinutes(3)),
            ("after replay", replayFrom.AddMinutes(3), DateTimeOffset.MaxValue),
            ("whole run", started, DateTimeOffset.MaxValue),
        };
        var all = _s.Posts.ToList();
        var summary = new List<object>();
        _out.WriteLine();
        _out.WriteLine("phase                           posts  results  rows/s  p50ms  p95ms  p99ms   maxms  errors");
        foreach (var (name, from, to) in phases)
        {
            var w = all.Where(p => p.At >= from && p.At < to).ToList();
            if (w.Count == 0) continue;
            var span = Math.Max(1, ((to == DateTimeOffset.MaxValue ? w.Max(p => p.At) : to) - from).TotalSeconds);
            var ms = w.Where(p => p.Status is >= 200 and < 300).Select(p => p.Ms).Order().ToList();
            var accepted = w.Sum(p => p.Accepted);
            var errors = w.Count(p => p.Status is 0 or >= 300);
            _out.WriteLine($"{name,-30}  {w.Count,5}  {accepted,7}  {accepted / span,6:0.0}  {Stats.Pct(ms, 50),5:0}  {Stats.Pct(ms, 95),5:0}  {Stats.Pct(ms, 99),5:0}  {(ms.Count == 0 ? 0 : ms[^1]),6:0}  {errors,6}");
            summary.Add(new { phase = name, posts = w.Count, accepted, rowsPerSec = Math.Round(accepted / span, 1), p50 = Stats.Pct(ms, 50), p95 = Stats.Pct(ms, 95), p99 = Stats.Pct(ms, 99), max = ms.Count == 0 ? 0 : ms[^1], errors });
        }
        var cfg = _s.ConfigFetches.Select(c => c.Ms).Order().ToList();
        _out.WriteLine();
        _out.WriteLine($"hub: {_s.Connects} connects, {_s.ConnectErrors} connect errors, {_s.HubDrops} drops; {_s.Heartbeats} heartbeats, {_s.HeartbeatErrors} heartbeat errors; " +
                       $"GetConfig p50 {Stats.Pct(cfg, 50):0} ms p95 {Stats.Pct(cfg, 95):0} ms max {(cfg.Count == 0 ? 0 : cfg[^1]):0} ms; " +
                       $"pushes: {_s.ConfigPushes} config, {_s.UpdatePushes} update; duplicates acknowledged {all.Sum(p => p.Duplicates)}; results still queued at stop {_s.LeftQueued}");
        _out.WriteLine("RESULT " + JsonSerializer.Serialize(new
        {
            o.Agents, o.Checks, o.Minutes, phases = summary,
            hub = new { _s.Connects, _s.ConnectErrors, _s.HubDrops, _s.Heartbeats, _s.HeartbeatErrors },
            getConfigP95 = Stats.Pct(cfg, 95), leftQueued = _s.LeftQueued,
        }));
        _out.Flush();
    }

    public void Dispose() => _timer.Dispose();
}
