using Nms.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Nms.Agent.Network;
using Nms.Agent.Scheduling;
using Nms.Agent.Storage;
using Nms.Shared.Contracts;
using Nms.Shared.Hubs;

namespace Nms.Agent.Transport;

/// <summary>
/// Maintains the persistent SignalR connection: receives config pushes and commands, sends heartbeats.
/// Automatic reconnect with exponential backoff; the scheduler keeps running on its last config while offline.
/// </summary>
public sealed class HubConnectionService(
    IOptions<AgentOptions> options,
    CredentialStore creds,
    EnrollmentService enrollment,
    InterfaceDiscovery interfaces,
    CheckScheduler scheduler,
    DiscoveryService discovery,
    ResultOutbox outbox,
    SelfUpdater updater,
    ILogger<HubConnectionService> log) : BackgroundService
{
    private HubConnection? _hub;
    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var c = await enrollment.EnsureEnrolledAsync(ct);
        var url = options.Value.ApiBaseUrl.TrimEnd('/') + HubRoutes.Agent;

        _hub = new HubConnectionBuilder()
            .WithUrl(url, o =>
            {
                o.Headers[AgentAuth.AgentIdHeader] = c.AgentId.ToString();
                o.Headers[AgentAuth.AgentSecretHeader] = c.Secret;
                if (options.Value.DangerousAcceptAnyServerCertificate)
                    o.HttpMessageHandlerFactory = h => { if (h is HttpClientHandler hh) hh.ServerCertificateCustomValidationCallback = (_, _, _, _) => true; return h; };
            })
            .WithAutomaticReconnect(new BackoffRetryPolicy())
            .AddJsonProtocol(o => o.PayloadSerializerOptions = NmsJson.Options)
            .Build();

        _hub.On<AgentConfigDto>(nameof(IAgentClient.ConfigUpdated), cfg => { scheduler.ApplyConfig(cfg); return Task.CompletedTask; });
        _hub.On<Guid>(nameof(IAgentClient.RunCheckNow), id => { scheduler.RunNow(id); return Task.CompletedTask; });
        _hub.On<DiscoveryRequest>(nameof(IAgentClient.StartDiscovery), async req =>
        {
            var result = await discovery.RunAsync(req, c.AgentId, ct);
            await SendSafe(nameof(IAgentHubServer.DiscoveryCompleted), result, ct);
        });
        _hub.On<AgentVersionInfo>(nameof(IAgentClient.UpdateAvailable), v => updater.TryUpdateAsync(v, ct));
        _hub.On<string>(nameof(IAgentClient.Revoked), reason =>
        {
            log.LogCritical("Agent revoked by server: {Reason}. Clearing credentials and stopping.", reason);
            creds.Delete();
            Environment.Exit(3);
            return Task.CompletedTask;
        });

        _hub.Reconnected += async _ =>
        {
            log.LogInformation("Reconnected to hub");
            await RequestConfig(ct);
        };
        _hub.Closed += ex => { log.LogWarning(ex, "Hub connection closed"); return Task.CompletedTask; };

        // Initial connect loop
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _hub.StartAsync(ct);
                log.LogInformation("Connected to {Url}", url);
                await RequestConfig(ct);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Hub connect failed ({Message}); retry in {Delay}", ex.Message, delay);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 120));
            }
        }

        // Heartbeat loop
        while (!ct.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(10, scheduler.CurrentConfig?.HeartbeatIntervalSec ?? 30));
            await Task.Delay(interval, ct);
            if (_hub.State != HubConnectionState.Connected) continue;
            var hb = new HeartbeatDto(AgentVersion.Current, DateTimeOffset.UtcNow, interfaces.Enumerate(), (int)Math.Min(int.MaxValue, outbox.Count()));
            await SendSafe(nameof(IAgentHubServer.Heartbeat), hb, ct);
        }
    }

    private async Task RequestConfig(CancellationToken ct)
    {
        try
        {
            var cfg = await _hub!.InvokeAsync<AgentConfigDto>(nameof(IAgentHubServer.GetConfig), ct);
            scheduler.ApplyConfig(cfg);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "GetConfig failed; keeping last known config");
        }
    }

    private async Task SendSafe<T>(string method, T arg, CancellationToken ct)
    {
        try { await _hub!.SendAsync(method, arg, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "{Method} failed", method); }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        if (_hub is not null) await _hub.DisposeAsync();
        await base.StopAsync(ct);
    }

    private sealed class BackoffRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext ctx) =>
            TimeSpan.FromSeconds(Math.Min(120, Math.Pow(2, Math.Min(7, ctx.PreviousRetryCount))) + Random.Shared.NextDouble());
    }
}
