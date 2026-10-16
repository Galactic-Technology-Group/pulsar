using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nms.Api.Auth;
using Nms.Shared.Hubs;

namespace Nms.Api.Hubs;

/// <summary>Browsers (and the worker) subscribe per site to receive live state changes.</summary>
[Authorize(Policy = Policies.User)]
public sealed class PortalHub : Hub<IPortalClient>
{
    public static string SiteGroup(Guid siteId) => $"site:{siteId}";
    public const string AllGroup = "site:all";

    public Task SubscribeSite(Guid siteId) => Groups.AddToGroupAsync(Context.ConnectionId, SiteGroup(siteId));
    public Task UnsubscribeSite(Guid siteId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, SiteGroup(siteId));
    public Task SubscribeAll() => Groups.AddToGroupAsync(Context.ConnectionId, AllGroup);
}
