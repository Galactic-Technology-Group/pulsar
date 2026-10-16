using System.Net.Http.Json;
using System.Text.Json;
using Nms.Data.Entities;
using Nms.Shared;

namespace Nms.Worker.Notifications;

/// <summary>
/// Teams incoming webhook (Workflows "Post to a channel when a webhook request is received" or legacy connector).
/// Channel config: {"url":"https://..."} or {"secretName":"teams-webhook-ops"} resolved via <see cref="ISecretResolver"/>.
/// </summary>
public sealed class TeamsWebhookSender(IHttpClientFactory http, ISecretResolver secrets, ILogger<TeamsWebhookSender> log) : INotificationSender
{
    public NotificationChannelType Type => NotificationChannelType.TeamsWebhook;

    private sealed record Config(string? Url, string? SecretName);

    public async Task SendAsync(NotificationChannel channel, NotificationMessage m, CancellationToken ct)
    {
        var cfg = JsonSerializer.Deserialize<Config>(channel.ConfigJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var url = cfg?.Url ?? (cfg?.SecretName is { } s ? await secrets.GetAsync(s, ct) : null);
        if (string.IsNullOrEmpty(url)) { log.LogWarning("Teams channel {Name} has no webhook url", channel.Name); return; }

        var card = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard", version = "1.4", schema = "http://adaptivecards.io/schemas/adaptive-card.json",
                        msteams = new { width = "Full" },
                        body = new object[]
                        {
                            new { type = "TextBlock", size = "Large", weight = "Bolder", color = m.IsRecovery ? "Good" : "Attention", text = m.Title },
                            new { type = "TextBlock", wrap = true, text = m.Summary },
                            new
                            {
                                type = "FactSet",
                                facts = m.Lines.Take(25).Select(l => new { title = l.Site, value = $"{l.Text}  ({l.At:HH:mm:ss} UTC)" }).ToArray()
                            }
                        },
                        actions = string.IsNullOrEmpty(m.PortalUrl) ? Array.Empty<object>() : new object[] { new { type = "Action.OpenUrl", title = "Open portal", url = m.PortalUrl } }
                    }
                }
            }
        };

        using var client = http.CreateClient("teams");
        using var resp = await client.PostAsJsonAsync(url, card, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Teams webhook returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
        log.LogInformation("Teams card '{Title}' posted to {Channel}", m.Title, channel.Name);
    }
}

public interface ISecretResolver
{
    Task<string?> GetAsync(string name, CancellationToken ct);
}

/// <summary>Resolves secret names from configuration (Key Vault is mounted into configuration in Azure).</summary>
public sealed class ConfigurationSecretResolver(IConfiguration config) : ISecretResolver
{
    public Task<string?> GetAsync(string name, CancellationToken ct) => Task.FromResult(config[$"Secrets:{name}"] ?? config[name]);
}
