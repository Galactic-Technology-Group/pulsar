using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nms.Data.Entities;
using Nms.Shared.Contracts;

namespace Nms.Portal.Services;

/// <summary>Typed client for the management API. Entities are deserialised with cycles ignored.</summary>
public sealed class NmsApiClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
    };

    // Dashboard / sites
    public Task<List<SiteSummary>> GetDashboardAsync() => Get<List<SiteSummary>>("api/dashboard/sites");
    public Task<List<Site>> GetSitesAsync() => Get<List<Site>>("api/sites");
    public Task<Site?> GetSiteAsync(Guid id) => GetOrNull<Site>($"api/sites/{id}");
    public Task<Site> CreateSiteAsync(SiteInput input) => Post<Site>("api/sites", input);
    public Task<Site> UpdateSiteAsync(Guid id, SiteInput input) => Put<Site>($"api/sites/{id}", input);
    public Task DeleteSiteAsync(Guid id) => Delete($"api/sites/{id}");

    // Agents
    public Task<List<Agent>> GetAgentsAsync(Guid siteId) => Get<List<Agent>>($"api/sites/{siteId}/agents");
    public Task<AgentCreated> CreateAgentAsync(Guid siteId, AgentInput input) => Post<AgentCreated>($"api/sites/{siteId}/agents", input);
    public Task<AgentCreated> ReissueTokenAsync(Guid agentId) => Post<AgentCreated>($"api/agents/{agentId}/reissue-token", new { });
    public Task RevokeAgentAsync(Guid agentId) => PostNoContent($"api/agents/{agentId}/revoke");
    public Task DeleteAgentAsync(Guid agentId) => Delete($"api/agents/{agentId}");
    public async Task<Guid> StartDiscoveryAsync(Guid agentId, string cidr, int? vlanId)
    {
        var resp = await http.PostAsJsonAsync($"api/agents/{agentId}/discover", new DiscoveryRequest(Guid.Empty, cidr, vlanId), Json);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.GetProperty("requestId").GetGuid();
    }
    public async Task<DiscoveryResult?> GetDiscoveryAsync(Guid requestId)
    {
        var resp = await http.GetAsync($"api/discoveries/{requestId}");
        if (resp.StatusCode == System.Net.HttpStatusCode.Accepted) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<DiscoveryResult>(Json);
    }

    // Devices & checks
    public Task<List<Device>> GetDevicesAsync(Guid siteId) => Get<List<Device>>($"api/sites/{siteId}/devices");
    public Task<Device?> GetDeviceAsync(Guid id) => GetOrNull<Device>($"api/devices/{id}");
    public Task<Device> CreateDeviceAsync(Guid siteId, DeviceInput input) => Post<Device>($"api/sites/{siteId}/devices", input);
    public Task<Device> UpdateDeviceAsync(Guid id, DeviceInput input) => Put<Device>($"api/devices/{id}", input);
    public Task DeleteDeviceAsync(Guid id) => Delete($"api/devices/{id}");
    public Task<Check> CreateCheckAsync(Guid deviceId, CheckInput input) => Post<Check>($"api/devices/{deviceId}/checks", input);
    public Task<Check> UpdateCheckAsync(Guid id, CheckInput input) => Put<Check>($"api/checks/{id}", input);
    public Task DeleteCheckAsync(Guid id) => Delete($"api/checks/{id}");
    public Task RunCheckAsync(Guid id) => PostNoContent($"api/checks/{id}/run");
    public Task<List<LatencyPoint>> GetHistoryAsync(Guid checkId, DateTimeOffset from, DateTimeOffset to) =>
        Get<List<LatencyPoint>>($"api/checks/{checkId}/history?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}");
    public Task<List<CheckResult>> GetRecentResultsAsync(Guid checkId, int limit = 50) => Get<List<CheckResult>>($"api/checks/{checkId}/results?limit={limit}");

    // Incidents
    public Task<List<Incident>> GetIncidentsAsync(bool open = true, Guid? siteId = null, int limit = 200) =>
        Get<List<Incident>>($"api/incidents?open={open}&limit={limit}{(siteId is null ? "" : $"&siteId={siteId}")}");
    public Task<Incident> AckIncidentAsync(Guid id) => Post<Incident>($"api/incidents/{id}/ack", new { });

    // Settings
    public Task<List<NotificationChannel>> GetChannelsAsync() => Get<List<NotificationChannel>>("api/channels");
    public Task<NotificationChannel> CreateChannelAsync(ChannelInput input) => Post<NotificationChannel>("api/channels", input);
    public Task<NotificationChannel> UpdateChannelAsync(Guid id, ChannelInput input) => Put<NotificationChannel>($"api/channels/{id}", input);
    public Task DeleteChannelAsync(Guid id) => Delete($"api/channels/{id}");
    public Task<List<AlertRule>> GetRulesAsync() => Get<List<AlertRule>>("api/rules");
    public Task<AlertRule> CreateRuleAsync(RuleInput input) => Post<AlertRule>("api/rules", input);
    public Task<AlertRule> UpdateRuleAsync(Guid id, RuleInput input) => Put<AlertRule>($"api/rules/{id}", input);
    public Task DeleteRuleAsync(Guid id) => Delete($"api/rules/{id}");
    public Task<List<MaintenanceWindow>> GetMaintenanceAsync() => Get<List<MaintenanceWindow>>("api/maintenance");
    public Task<MaintenanceWindow> CreateMaintenanceAsync(MaintenanceInput input) => Post<MaintenanceWindow>("api/maintenance", input);
    public Task DeleteMaintenanceAsync(Guid id) => Delete($"api/maintenance/{id}");
    public Task<List<AgentRelease>> GetReleasesAsync() => Get<List<AgentRelease>>("api/releases");
    public Task<AgentRelease> CreateReleaseAsync(ReleaseInput input) => Post<AgentRelease>("api/releases", input);

    // ---- helpers ----
    private async Task<T> Get<T>(string url) => (await http.GetFromJsonAsync<T>(url, Json))!;
    private async Task<T?> GetOrNull<T>(string url) where T : class
    {
        using var resp = await http.GetAsync(url);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(Json);
    }
    private async Task<T> Post<T>(string url, object body)
    {
        using var resp = await http.PostAsJsonAsync(url, body, Json);
        await Ensure(resp);
        return (await resp.Content.ReadFromJsonAsync<T>(Json))!;
    }
    private async Task<T> Put<T>(string url, object body)
    {
        using var resp = await http.PutAsJsonAsync(url, body, Json);
        await Ensure(resp);
        return (await resp.Content.ReadFromJsonAsync<T>(Json))!;
    }
    private async Task PostNoContent(string url) { using var resp = await http.PostAsync(url, null); await Ensure(resp); }
    private async Task Delete(string url) { using var resp = await http.DeleteAsync(url); await Ensure(resp); }
    private static async Task Ensure(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        throw new ApiException((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }
}

public sealed class ApiException(int status, string body) : Exception($"API returned {status}: {body}")
{
    public int Status { get; } = status;
}
