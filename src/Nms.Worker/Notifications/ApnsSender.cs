using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;

namespace Nms.Worker.Notifications;

/// <summary>Apple Push Notification service settings ("Apns" section). Push stays off until all four are set.</summary>
public sealed class ApnsOptions
{
    /// <summary>Key ID of the APNs auth key (10 characters, shown next to the key in the Apple Developer portal).</summary>
    public string KeyId { get; set; } = "";
    /// <summary>Apple Developer team ID (10 characters).</summary>
    public string TeamId { get; set; } = "";
    /// <summary>The app's bundle identifier; APNs calls it the topic.</summary>
    public string BundleId { get; set; } = "";
    /// <summary>Contents of the .p8 key file (PEM), or just its base64 body.</summary>
    public string PrivateKey { get; set; } = "";

    public bool IsConfigured => KeyId.Length > 0 && TeamId.Length > 0 && BundleId.Length > 0 && PrivateKey.Length > 0;
}

/// <summary>
/// Push channel: sends each alert to every phone registered through the iOS app, over APNs with token (.p8) auth.
/// The channel has no settings of its own; which incidents reach it is decided by the alert rules that point at it.
/// </summary>
public sealed class ApnsSender(IHttpClientFactory http, IServiceScopeFactory scopes, IOptions<ApnsOptions> options, ILogger<ApnsSender> log) : INotificationSender
{
    public NotificationChannelType Type => NotificationChannelType.Push;

    private readonly object gate = new();
    private string? jwt;
    private DateTimeOffset jwtIssued;

    public async Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.IsConfigured) { log.LogWarning("Push channel {Name}: APNs is not configured (Apns:KeyId, TeamId, BundleId, PrivateKey); nothing sent", channel.Name); return; }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
        var devices = await db.PushDevices.AsNoTracking().Select(d => new { d.Id, d.Token, d.Environment }).ToListAsync(ct);
        if (devices.Count == 0) { log.LogInformation("Push channel {Name}: no phones registered", channel.Name); return; }

        var payload = JsonSerializer.Serialize(Payload(message));
        var bearer = Token(o);
        using var client = http.CreateClient("apns");
        var gone = new List<Guid>();
        var sent = 0;
        foreach (var d in devices)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Host(d.Environment)}/3/device/{d.Token}")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,   // APNs speaks HTTP/2 only
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("authorization", "bearer " + bearer);
            request.Headers.TryAddWithoutValidation("apns-topic", o.BundleId);
            request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            request.Headers.TryAddWithoutValidation("apns-priority", "10");
            // A phone that was off should not get a stale outage hours later.
            request.Headers.TryAddWithoutValidation("apns-expiration", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());
            try
            {
                using var response = await client.SendAsync(request, ct);
                if (response.IsSuccessStatusCode) { sent++; continue; }
                var reason = await response.Content.ReadAsStringAsync(ct);
                if (IsDeadToken(response.StatusCode, reason)) gone.Add(d.Id);
                else log.LogWarning("APNs refused a push ({Status}): {Reason}", (int)response.StatusCode, reason);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "APNs send failed"); }
        }

        if (gone.Count > 0)
        {
            await db.PushDevices.Where(d => gone.Contains(d.Id)).ExecuteDeleteAsync(ct);
            log.LogInformation("Removed {Count} phone(s) whose push token is no longer valid", gone.Count);
        }
        log.LogInformation("Push '{Title}' sent to {Sent} of {Total} phone(s)", message.Title, sent, devices.Count);
    }

    internal static string Host(string environment) => environment == "sandbox" ? "api.sandbox.push.apple.com" : "api.push.apple.com";

    /// <summary>Apple answers 410, or 400 BadDeviceToken, when the app was removed or the token belongs to the other environment.</summary>
    internal static bool IsDeadToken(HttpStatusCode status, string body) =>
        status == HttpStatusCode.Gone || status == HttpStatusCode.BadRequest && (body.Contains("BadDeviceToken") || body.Contains("DeviceTokenNotForTopic"));

    /// <summary>The notification as the phone shows it: site as the title for one incident, a count for a digest.</summary>
    internal static object Payload(NotificationMessage m)
    {
        string title, body;
        if (m.Lines.Count == 1)
        {
            title = (m.IsRecovery ? "Recovered · " : "Down · ") + m.Lines[0].Site;
            body = m.Lines[0].Text;
        }
        else
        {
            title = m.Title;
            body = string.Join("\n", m.Lines.Take(3).Select(l => $"{l.Site}: {l.Text}")) + (m.Lines.Count > 3 ? $"\n+{m.Lines.Count - 3} more" : "");
        }
        return new Dictionary<string, object>
        {
            ["aps"] = new Dictionary<string, object>
            {
                ["alert"] = new Dictionary<string, string> { ["title"] = Clip(title, 120), ["body"] = Clip(body, 600) },
                ["sound"] = "default",
                // Outages and recoveries stack separately in Notification Centre.
                ["thread-id"] = m.IsRecovery ? "recovered" : "down",
            },
            ["recovery"] = m.IsRecovery,
            ["count"] = m.Lines.Count,
        };
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>The provider token: an ES256 JWT that Apple accepts for up to an hour; reused for 45 minutes.</summary>
    private string Token(ApnsOptions o)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (jwt is null || now - jwtIssued > TimeSpan.FromMinutes(45)) { jwt = CreateToken(o.KeyId, o.TeamId, o.PrivateKey, now); jwtIssued = now; }
            return jwt;
        }
    }

    internal static string CreateToken(string keyId, string teamId, string privateKey, DateTimeOffset now)
    {
        using var key = ECDsa.Create();
        var pem = privateKey.Trim();
        if (pem.Contains("-----BEGIN")) key.ImportFromPem(pem);
        else key.ImportPkcs8PrivateKey(Convert.FromBase64String(pem.ReplaceLineEndings("")), out _);

        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = keyId }));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = teamId, iat = now.ToUnixTimeSeconds() }));
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{claims}.{B64(signature)}";
    }

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
