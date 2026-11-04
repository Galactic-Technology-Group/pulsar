using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using Nms.Agent.Network;
using Nms.Agent.Probes;
using Nms.Agent.Storage;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Scheduling;

/// <summary>
/// Runs every enabled check on its own interval with per-check jitter and bounded concurrency per probe type.
/// Config can be swapped at any time; checks that disappear are cancelled, new ones start immediately.
/// </summary>
public sealed class CheckScheduler : IDisposable
{
    private readonly Dictionary<CheckType, IProbe> _probes;
    private readonly Dictionary<CheckType, SemaphoreSlim> _limits;
    private readonly ResultOutbox _outbox;
    private readonly InterfaceDiscovery _interfaces;
    private readonly ILogger<CheckScheduler> _log;
    private readonly ConcurrentDictionary<Guid, Runner> _runners = new();
    private IReadOnlyList<InterfaceInfo> _ifaces = Array.Empty<InterfaceInfo>();
    private DateTimeOffset _ifacesRefreshed = DateTimeOffset.MinValue;

    public AgentConfigDto? CurrentConfig { get; private set; }
    public event Action<CheckResultDto>? ResultProduced;

    public CheckScheduler(IEnumerable<IProbe> probes, IOptions<AgentOptions> options, ResultOutbox outbox, InterfaceDiscovery interfaces, ILogger<CheckScheduler> log)
    {
        _probes = probes.ToDictionary(p => p.Type);
        _outbox = outbox;
        _interfaces = interfaces;
        _log = log;
        _limits = new()
        {
            [CheckType.Icmp] = new SemaphoreSlim(options.Value.MaxConcurrentIcmp),
            [CheckType.TcpPort] = new SemaphoreSlim(options.Value.MaxConcurrentTcp),
            [CheckType.Http] = new SemaphoreSlim(options.Value.MaxConcurrentHttp),
        };
    }

    public void ApplyConfig(AgentConfigDto cfg)
    {
        if (CurrentConfig is not null && cfg.Version < CurrentConfig.Version)
        {
            _log.LogDebug("Ignoring stale config v{Version}", cfg.Version);
            return;
        }
        CurrentConfig = cfg;
        var wanted = cfg.Checks.Where(c => c.Enabled).ToDictionary(c => c.CheckId);

        foreach (var (id, runner) in _runners)
        {
            if (!wanted.TryGetValue(id, out var def) || !def.Equals(runner.Definition))
            {
                runner.Stop();
                _runners.TryRemove(id, out _);
            }
        }
        foreach (var def in wanted.Values)
        {
            if (_runners.ContainsKey(def.CheckId)) continue;
            var r = new Runner(def, this);
            if (_runners.TryAdd(def.CheckId, r)) r.Start();
        }
        _log.LogInformation("Applied config v{Version}: {Count} active checks", cfg.Version, _runners.Count);
    }

    public void RunNow(Guid checkId)
    {
        if (_runners.TryGetValue(checkId, out var r)) r.Trigger();
    }

    private IReadOnlyList<InterfaceInfo> Interfaces()
    {
        if (DateTimeOffset.UtcNow - _ifacesRefreshed > TimeSpan.FromMinutes(1))
        {
            _ifaces = _interfaces.Enumerate();
            _ifacesRefreshed = DateTimeOffset.UtcNow;
        }
        return _ifaces;
    }

    private async Task ExecuteAsync(CheckDefinition def, CancellationToken ct)
    {
        if (!_probes.TryGetValue(def.Type, out var probe)) return;
        if (!IPAddress.TryParse(def.TargetAddress, out var target))
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(def.TargetAddress, System.Net.Sockets.AddressFamily.InterNetwork, ct);
                target = addrs.FirstOrDefault();
            }
            catch { target = null; }
            if (target is null)
            {
                Emit(new CheckResultDto(def.CheckId, DateTimeOffset.UtcNow, false, null, "DnsFailure"));
                return;
            }
        }

        var source = SourceInterfaceResolver.Resolve(CurrentConfig?.VlanMode ?? VlanMode.Routed, def.VlanId, target, Interfaces());
        var limit = _limits[def.Type];
        await limit.WaitAsync(ct);
        try
        {
            var outcome = await probe.RunAsync(def, target, source, ct);
            Emit(new CheckResultDto(def.CheckId, DateTimeOffset.UtcNow, outcome.Ok, outcome.LatencyMs, outcome.Detail));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Probe {Type} for {Target} threw", def.Type, def.TargetAddress);
            Emit(new CheckResultDto(def.CheckId, DateTimeOffset.UtcNow, false, null, ex.GetType().Name));
        }
        finally { limit.Release(); }
    }

    private void Emit(CheckResultDto r)
    {
        _outbox.Enqueue(r);
        ResultProduced?.Invoke(r);
    }

    public void Dispose()
    {
        foreach (var r in _runners.Values) r.Stop();
        _runners.Clear();
    }

    private sealed class Runner(CheckDefinition def, CheckScheduler owner)
    {
        public CheckDefinition Definition { get; } = def;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _trigger = new(0);

        public void Start() => _ = Task.Run(LoopAsync);
        public void Stop() => _cts.Cancel();
        public void Trigger() { if (_trigger.CurrentCount == 0) _trigger.Release(); }

        private async Task LoopAsync()
        {
            var ct = _cts.Token;
            var interval = TimeSpan.FromSeconds(Math.Max(5, Definition.IntervalSec));
            // Spread initial runs across the interval so 500 checks don't fire at once.
            var initial = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * interval.TotalMilliseconds);
            try
            {
                await WaitAsync(initial, ct);
                while (!ct.IsCancellationRequested)
                {
                    await owner.ExecuteAsync(Definition, ct);
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(-500, 500));
                    await WaitAsync(interval + jitter, ct);
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>Waits for the delay or an explicit trigger, whichever comes first.</summary>
        private async Task WaitAsync(TimeSpan delay, CancellationToken ct) => await _trigger.WaitAsync(delay, ct);
    }
}
