using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nms.Shared;
using Nms.Worker.Notifications;

namespace Nms.Worker.Tests;

public class ApnsSenderTests
{
    private static NotificationMessage Message(bool recovery, params (string site, string text)[] lines) =>
        new(recovery ? "RECOVERED" : $"{lines.Length} outages across 2 site(s)", "summary",
            lines.Select(l => new NotificationLine(l.site, l.text, DateTimeOffset.UtcNow, IncidentKind.CheckDown)).ToList(), recovery, null);

    private static JsonElement Aps(NotificationMessage m) =>
        JsonDocument.Parse(JsonSerializer.Serialize(ApnsSender.Payload(m))).RootElement.GetProperty("aps");

    [Fact]
    public void One_incident_is_titled_with_its_site()
    {
        var aps = Aps(Message(false, ("Main office", "core-sw (10.0.0.1): ICMP down")));
        Assert.Equal("Down · Main office", aps.GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal("core-sw (10.0.0.1): ICMP down", aps.GetProperty("alert").GetProperty("body").GetString());
        Assert.Equal("default", aps.GetProperty("sound").GetString());
        Assert.Equal("down", aps.GetProperty("thread-id").GetString());
    }

    [Fact]
    public void A_recovery_says_so_and_stacks_apart_from_outages()
    {
        var aps = Aps(Message(true, ("Main office", "Recovered: core-sw")));
        Assert.Equal("Recovered · Main office", aps.GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal("recovered", aps.GetProperty("thread-id").GetString());
    }

    [Fact]
    public void A_digest_lists_three_lines_and_counts_the_rest()
    {
        var aps = Aps(Message(false, ("A", "one"), ("A", "two"), ("B", "three"), ("B", "four"), ("B", "five")));
        var body = aps.GetProperty("alert").GetProperty("body").GetString()!;
        Assert.Equal("5 outages across 2 site(s)", aps.GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal("A: one\nA: two\nB: three\n+2 more", body);
    }

    [Fact]
    public void Long_text_is_clipped_to_fit_a_notification()
    {
        var aps = Aps(Message(false, ("Site", new string('x', 2000))));
        Assert.Equal(600, aps.GetProperty("alert").GetProperty("body").GetString()!.Length);
    }

    [Theory]
    [InlineData("sandbox", "api.sandbox.push.apple.com")]
    [InlineData("production", "api.push.apple.com")]
    public void Each_build_type_uses_its_own_apple_host(string environment, string host) => Assert.Equal(host, ApnsSender.Host(environment));

    [Theory]
    [InlineData(HttpStatusCode.Gone, "{\"reason\":\"Unregistered\"}", true)]
    [InlineData(HttpStatusCode.BadRequest, "{\"reason\":\"BadDeviceToken\"}", true)]
    [InlineData(HttpStatusCode.BadRequest, "{\"reason\":\"BadTopic\"}", false)]
    [InlineData(HttpStatusCode.Forbidden, "{\"reason\":\"ExpiredProviderToken\"}", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "", false)]
    public void Only_tokens_apple_calls_dead_are_removed(HttpStatusCode status, string body, bool dead) =>
        Assert.Equal(dead, ApnsSender.IsDeadToken(status, body));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_provider_token_is_an_es256_jwt_signed_with_the_key(bool pem)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p8 = pem ? key.ExportPkcs8PrivateKeyPem() : Convert.ToBase64String(key.ExportPkcs8PrivateKey());
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var parts = ApnsSender.CreateToken("KEY1234567", "TEAM123456", p8, now).Split('.');

        Assert.Equal(3, parts.Length);
        var header = JsonDocument.Parse(Decode(parts[0])).RootElement;
        var claims = JsonDocument.Parse(Decode(parts[1])).RootElement;
        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        Assert.Equal("KEY1234567", header.GetProperty("kid").GetString());
        Assert.Equal("TEAM123456", claims.GetProperty("iss").GetString());
        Assert.Equal(1_800_000_000, claims.GetProperty("iat").GetInt64());
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Decode(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
