using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Communication.Email;
using Microsoft.Extensions.Options;
using Nms.Data.Entities;
using Nms.Shared;

namespace Nms.Worker.Notifications;

/// <summary>Azure Communication Services email. Channel config: {"to":["ops@example.com", ...]}.</summary>
public sealed class EmailSender(IOptions<EmailOptions> options, ILogger<EmailSender> log) : INotificationSender
{
    public NotificationChannelType Type => NotificationChannelType.Email;
    private readonly Lazy<EmailClient> _client = new(() => new EmailClient(options.Value.ConnectionString));

    private sealed record Config(string[] To);

    public async Task SendAsync(NotificationChannel channel, NotificationMessage m, CancellationToken ct)
    {
        var cfg = JsonSerializer.Deserialize<Config>(channel.ConfigJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (cfg?.To is not { Length: > 0 }) { log.LogWarning("Email channel {Name} has no recipients", channel.Name); return; }
        if (string.IsNullOrEmpty(options.Value.ConnectionString)) { log.LogWarning("Email not configured; would send '{Title}' to {To}", m.Title, string.Join(",", cfg.To)); return; }

        var html = new StringBuilder();
        html.Append($"<h2 style=\"color:{(m.IsRecovery ? "#1a7f37" : "#c62828")}\">{WebUtility.HtmlEncode(m.Title)}</h2>");
        html.Append($"<p>{WebUtility.HtmlEncode(m.Summary)}</p><ul>");
        foreach (var l in m.Lines)
            html.Append($"<li><b>{WebUtility.HtmlEncode(l.Site)}</b>: {WebUtility.HtmlEncode(l.Text)} <i>({l.At:u})</i></li>");
        html.Append("</ul>");
        if (!string.IsNullOrEmpty(m.PortalUrl)) html.Append($"<p><a href=\"{m.PortalUrl}\">Open the NMS portal</a></p>");

        var content = new EmailContent(m.Title) { Html = html.ToString(), PlainText = m.Summary + "\n" + string.Join("\n", m.Lines.Select(l => $"{l.Site}: {l.Text} ({l.At:u})")) };
        var recipients = new EmailRecipients(cfg.To.Select(a => new EmailAddress(a)).ToList());
        var msg = new EmailMessage(options.Value.From, recipients, content);
        await _client.Value.SendAsync(Azure.WaitUntil.Started, msg, ct);
        log.LogInformation("Email '{Title}' queued to {Count} recipients", m.Title, cfg.To.Length);
    }
}
