using System.Net;
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
/// When the API rejects the stored credentials (401) and an enrollment token is configured, the agent re-enrols
/// and reconnects by itself instead of retrying a dead secret forever.
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

        while (!ct.IsCancellationRequested)
        {
            // Completed when SignalR gives up reconnecting (see BackoffRetryPolicy); we then start over,
            // which also re-runs the 401 → re-enrol path.
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _hub = Build(url, c, closed, ct);
            c = await ConnectAsync(url, c, closed, ct);
            await HeartbeatLoopAsync(closed, ct);

            if (ct.IsCancellationRequested) break;
            log.LogWarning("Hub connection lost and automatic reconnect gave up; reconnecting from scratch");
            await _hub.DisposeAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private HubConnection Build(string url, AgentCredentials c, TaskCompletionSource closed, CancellationToken ct)
    {
        var hub = new HubConnectionBuilder()
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

        hub.On<AgentConfigDto>(nameof(IAgentClient.ConfigUpdated), cfg => { scheduler.ApplyConfig(cfg); return Task.CompletedTask; });
        hub.On<Guid>(nameof(IAgentClient.RunCheckNow), id => { scheduler.RunNow(id); return Task.CompletedTask; });
        hub.On<DiscoveryRequest>(nameof(IAgentClient.StartDiscovery), async req =>
        {
            var result = await discovery.RunAsync(req, c.AgentId, ct);
            await SendSafe(hub, nameof(IAgentHubServer.DiscoveryCompleted), result, ct);
        });
        hub.On<AgentVersionInfo>(nameof(IAgentClient.UpdateAvailable), v => updater.TryUpdateAsync(v, ct));
        hub.On<string>(nameof(IAgentClient.Revoked), reason =>
        {
            log.LogCritical("Agent revoked by server: {Reason}. Clearing credentials and stopping.", reason);
            creds.Delete();
            Environment.Exit(3);
            return Task.CompletedTask;
        });

        hub.Reconnecting += ex => { log.LogWarning("Hub connection lost ({Message}); reconnecting", ex?.Message ?? "closed"); return Task.CompletedTask; };
        hub.Reconnected += async _ =>
        {
            log.LogInformation("Reconnected to hub");
            await RequestConfig(hub, ct);
        };
        hub.Closed += ex => { log.LogWarning(ex, "Hub connection closed"); closed.TrySetResult(); return Task.CompletedTask; };
        return hub;
    }

    /// <summary>Initial connect with backoff. Returns the credentials in use, which change if a 401 forced a re-enrol.</summary>
    private async Task<AgentCredentials> ConnectAsync(string url, AgentCredentials c, TaskCompletionSource closed, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _hub!.StartAsync(ct);
                log.LogInformation("Connected to {Url}", url);
                await RequestConfig(_hub, ct);
                return c;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized && enrollment.CanReEnroll)
            {
                await _hub!.DisposeAsync();
                c = await enrollment.ReEnrollAsync(ct);
                _hub = Build(url, c, closed, ct);
                delay = TimeSpan.FromSeconds(2);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                log.LogError("API rejected the stored credentials and no EnrollmentToken is configured. " +
                             "Re-issue a token in the portal and run install.sh --token <token>. Retry in {Delay}", delay);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Hub connect failed ({Message}); retry in {Delay}", ex.Message, delay);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 120));
            }
        }
        return c;
    }

    private async Task HeartbeatLoopAsync(TaskCompletionSource closed, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !closed.Task.IsCompleted)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(10, scheduler.CurrentConfig?.HeartbeatIntervalSec ?? 30));
            await Task.WhenAny(Task.Delay(interval, ct), closed.Task);
            if (ct.IsCancellationRequested || closed.Task.IsCompleted) break;
            if (_hub!.State != HubConnectionState.Connected) continue;
            var hb = new HeartbeatDto(AgentVersion.Current, DateTimeOffset.UtcNow, interfaces.Enumerate(), (int)Math.Min(int.MaxValue, outbox.Count()));
            await SendSafe(_hub, nameof(IAgentHubServer.Heartbeat), hb, ct);
        }
    }

    private async Task RequestConfig(HubConnection hub, CancellationToken ct)
    {
        try
        {
            var cfg = await hub.InvokeAsync<AgentConfigDto>(nameof(IAgentHubServer.GetConfig), ct);
            scheduler.ApplyConfig(cfg);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "GetConfig failed; keeping last known config");
        }
    }

    private async Task SendSafe<T>(HubConnection hub, string method, T arg, CancellationToken ct)
    {
        try { await hub.SendAsync(method, arg, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "{Method} failed", method); }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        if (_hub is not null) await _hub.DisposeAsync();
        await base.StopAsync(ct);
    }

    /// <summary>
    /// 1, 2, 4 … 120 s between attempts, then give up after <see cref="MaxAttempts"/> (~6 minutes) so Closed fires
    /// and the service rebuilds the connection, re-enrolling if the secret has meanwhile been revoked.
    /// </summary>
    internal sealed class BackoffRetryPolicy : IRetryPolicy
    {
        public const int MaxAttempts = 10;
        public TimeSpan? NextRetryDelay(RetryContext ctx) =>
            ctx.PreviousRetryCount >= MaxAttempts ? null
            : TimeSpan.FromSeconds(Math.Min(120, Math.Pow(2, Math.Min(7, ctx.PreviousRetryCount))) + Random.Shared.NextDouble());
    }
}
