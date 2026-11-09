using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Nms.Shared;
using Nms.Shared.Contracts;
using Nms.Shared.Hubs;

namespace Nms.LoadTest;

/// <summary>
/// One simulated site agent speaking the real protocol: hub connection with the agent secret, GetConfig, heartbeats,
/// and result batches of up to 500 posted every flush interval. A "replay" agent drops its hub connection and stops
/// uploading for the outage window (a WAN cut), keeps producing results, then drains the backlog back to back like the
/// real agent's outbox.
/// </summary>
public sealed class SimAgent(SeededAgent a, RunOptions o, Stats stats, bool replays, DateTimeOffset runStart, string reportVersion)
{
    private const int BatchSize = 500;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxConnectionsPerServer = 256 })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    private readonly List<CheckResultDto> _pending = new();
    private readonly Random _rng = new(a.AgentId.GetHashCode());

    public async Task RunAsync(TimeSpan startDelay, CancellationToken ct)
    {
        try { await Task.Delay(startDelay, ct); } catch (OperationCanceledException) { return; }

        var hub = BuildHub();
        AgentConfigDto? cfg = null;
        while (cfg is null && !ct.IsCancellationRequested) cfg = await ConnectAsync(hub, ct);
        if (cfg is null) return;

        var interval = TimeSpan.FromSeconds(Math.Max(5, cfg.ResultsFlushIntervalSec));
        var checks = cfg.Checks.Where(c => c.Enabled).Select((c, i) => new SimCheck(c.CheckId, i, TimeSpan.FromSeconds(c.IntervalSec),
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(_rng.NextDouble() * c.IntervalSec))).ToList();
        var outageStart = runStart + TimeSpan.FromMinutes(o.ReplayAtMinute);
        var outageEnd = outageStart + TimeSpan.FromMinutes(o.ReplayGapMinutes);
        var nextHeartbeat = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(cfg.HeartbeatIntervalSec > 3600 ? 30 : cfg.HeartbeatIntervalSec);
        var inOutage = false;

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = DateTimeOffset.UtcNow;
                Generate(checks, now);

                if (replays && now >= outageStart && now < outageEnd)
                {
                    if (!inOutage) { inOutage = true; Interlocked.Increment(ref stats.OutagesStarted); await SafeStop(hub); }
                    continue;
                }
                if (inOutage)
                {
                    inOutage = false;
                    stats.Note($"{a.Name} back after outage with {_pending.Count} queued results");
                }
                if (hub.State == HubConnectionState.Disconnected && await ConnectAsync(hub, ct) is null) continue;

                if (now >= nextHeartbeat && hub.State == HubConnectionState.Connected)
                {
                    nextHeartbeat = now + TimeSpan.FromSeconds(30);
                    try
                    {
                        await hub.SendAsync(nameof(IAgentHubServer.Heartbeat), new HeartbeatDto(reportVersion, now, [], _pending.Count), ct);
                        Interlocked.Increment(ref stats.Heartbeats);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Interlocked.Increment(ref stats.HeartbeatErrors); }
                }
                await UploadAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.Add(ref stats.LeftQueued, _pending.Count);
            await SafeStop(hub);
            await hub.DisposeAsync();
        }
    }

    private void Generate(List<SimCheck> checks, DateTimeOffset now)
    {
        var minute = (now - runStart).TotalMinutes;
        foreach (var c in checks)
        {
            while (c.NextDue <= now)
            {
                // 2 % of checks flap: down for 3 minutes in every 15, staggered, so incidents open and close all run long.
                var flapper = c.Index % 50 == 7;
                var down = flapper && ((minute + c.Index % 15) % 15) < 3;
                _pending.Add(new CheckResultDto(c.Id, c.NextDue, !down, down ? null : 1 + _rng.Next(30), down ? "TimedOut" : null));
                c.NextDue += c.Interval;
            }
        }
    }

    private async Task UploadAsync(CancellationToken ct)
    {
        while (_pending.Count > 0 && !ct.IsCancellationRequested)
        {
            var batch = _pending.Take(BatchSize).ToList();
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.Api}/api/results")
            {
                Content = JsonContent.Create(new ResultsBatch(a.AgentId, batch), options: NmsJson.Options),
            };
            req.Headers.Add(AgentAuth.AgentIdHeader, a.AgentId.ToString());
            req.Headers.Add(AgentAuth.AgentSecretHeader, a.Secret);
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await Http.SendAsync(req, ct);
                var ms = sw.Elapsed.TotalMilliseconds;
                if (resp.IsSuccessStatusCode)
                {
                    var ack = await resp.Content.ReadFromJsonAsync<ResultsAck>(NmsJson.Options, ct);
                    stats.Post(ms, (int)resp.StatusCode, batch.Count, ack?.Accepted ?? 0, ack?.Duplicates ?? 0);
                    _pending.RemoveRange(0, batch.Count);
                }
                else
                {
                    stats.Post(ms, (int)resp.StatusCode, batch.Count, 0, 0);
                    return;   // keep the batch, retry next tick (what the agent's outbox does)
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                stats.Post(sw.Elapsed.TotalMilliseconds, 0, batch.Count, 0, 0);
                stats.Note($"{a.Name} POST failed: {ex.GetType().Name} {ex.Message}");
                return;
            }
        }
    }

    private HubConnection BuildHub()
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(o.Api + HubRoutes.Agent, opt =>
            {
                opt.Headers[AgentAuth.AgentIdHeader] = a.AgentId.ToString();
                opt.Headers[AgentAuth.AgentSecretHeader] = a.Secret;
            })
            .WithAutomaticReconnect()
            .AddJsonProtocol(p => p.PayloadSerializerOptions = NmsJson.Options)
            .Build();
        hub.On<AgentConfigDto>(nameof(IAgentClient.ConfigUpdated), _ => { Interlocked.Increment(ref stats.ConfigPushes); return Task.CompletedTask; });
        hub.On<AgentVersionInfo>(nameof(IAgentClient.UpdateAvailable), _ => { Interlocked.Increment(ref stats.UpdatePushes); return Task.CompletedTask; });
        hub.On<Guid>(nameof(IAgentClient.RunCheckNow), _ => Task.CompletedTask);
        hub.On<string>(nameof(IAgentClient.Revoked), _ => Task.CompletedTask);
        hub.Reconnecting += _ => { Interlocked.Increment(ref stats.HubDrops); Interlocked.Decrement(ref stats.Connected); return Task.CompletedTask; };
        hub.Reconnected += _ => { Interlocked.Increment(ref stats.Connected); return Task.CompletedTask; };
        return hub;
    }

    private async Task<AgentConfigDto?> ConnectAsync(HubConnection hub, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await hub.StartAsync(ct);
            Interlocked.Increment(ref stats.Connected);
            Interlocked.Increment(ref stats.Connects);
            var cfg = await hub.InvokeAsync<AgentConfigDto>(nameof(IAgentHubServer.GetConfig), ct);
            stats.ConfigFetch(sw.Elapsed.TotalMilliseconds);
            return cfg;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref stats.ConnectErrors);
            stats.Note($"{a.Name} hub connect failed: {ex.GetType().Name} {ex.Message}");
            if (hub.State != HubConnectionState.Disconnected) await SafeStop(hub);
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { }
            return null;
        }
        catch (OperationCanceledException) { return null; }
    }

    private async Task SafeStop(HubConnection hub)
    {
        if (hub.State == HubConnectionState.Disconnected) return;
        var wasConnected = hub.State == HubConnectionState.Connected;
        try { await hub.StopAsync(); } catch { /* best effort */ }
        if (wasConnected) Interlocked.Decrement(ref stats.Connected);
    }

    private sealed class SimCheck(Guid id, int index, TimeSpan interval, DateTimeOffset firstDue)
    {
        public Guid Id { get; } = id;
        public int Index { get; } = index;
        public TimeSpan Interval { get; } = interval;
        public DateTimeOffset NextDue { get; set; } = firstDue;
    }
}
