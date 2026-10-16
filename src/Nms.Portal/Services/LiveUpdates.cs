using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Identity.Web;
using Nms.Shared;
using Nms.Shared.Hubs;

namespace Nms.Portal.Services;

/// <summary>Per-circuit SignalR connection to the API's portal hub. Pages subscribe to <see cref="Changed"/> and re-query.</summary>
public sealed class LiveUpdates(IConfiguration config, IServiceProvider services, ILogger<LiveUpdates> log) : IAsyncDisposable
{
    private HubConnection? _hub;
    public event Func<Task>? Changed;

    public async Task EnsureConnectedAsync(Guid? siteId = null)
    {
        if (_hub is null)
        {
            var url = (config["Api:BaseUrl"] ?? "").TrimEnd('/') + HubRoutes.Portal;
            _hub = new HubConnectionBuilder()
                .WithUrl(url, o =>
                {
                    if (config["Auth:Mode"] != "Development" && config["Api:Scope"] is { Length: > 0 } scope)
                        o.AccessTokenProvider = async () => await services.GetRequiredService<ITokenAcquisition>().GetAccessTokenForUserAsync([scope]);
                })
                .WithAutomaticReconnect()
                .Build();
            _hub.On<Guid, Guid, Guid, CheckState, DateTimeOffset>(nameof(IPortalClient.CheckStateChanged), (_, _, _, _, _) => Raise());
            _hub.On<Guid, Guid, AgentStatus, DateTimeOffset>(nameof(IPortalClient.AgentStatusChanged), (_, _, _, _) => Raise());
            _hub.On<Guid, Guid, bool>(nameof(IPortalClient.IncidentChanged), (_, _, _) => Raise());
            try { await _hub.StartAsync(); await _hub.SendAsync("SubscribeAll"); }
            catch (Exception ex) { log.LogWarning(ex, "Live updates unavailable"); }
        }
        if (siteId is not null && _hub.State == HubConnectionState.Connected)
            await _hub.SendAsync("SubscribeSite", siteId.Value);
    }

    private Task Raise() => Changed?.Invoke() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync() { if (_hub is not null) await _hub.DisposeAsync(); }
}
