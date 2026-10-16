using Nms.Api.Auth;
using Nms.Data;

namespace Nms.Api.Tests;

public class SecretHasherTests
{
    [Fact]
    public void Verify_roundtrips_and_rejects_wrong_secret()
    {
        var secret = SecretHasher.NewToken();
        var stored = SecretHasher.HashSecret(secret);
        Assert.StartsWith("pbkdf2$", stored);
        Assert.True(SecretHasher.Verify(secret, stored));
        Assert.False(SecretHasher.Verify(secret + "x", stored));
        Assert.False(SecretHasher.Verify(secret, "garbage"));
    }

    [Fact]
    public void Tokens_are_url_safe_and_unique()
    {
        var a = SecretHasher.NewToken(); var b = SecretHasher.NewToken();
        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a); Assert.DoesNotContain('/', a); Assert.DoesNotContain('=', a);
        Assert.Equal(43, a.Length);
    }

    [Fact]
    public void Sha256_is_stable_lowercase_hex()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", SecretHasher.Sha256("abc"));
    }
}

public class NamingTests
{
    [Theory]
    [InlineData("SiteId", "site_id")]
    [InlineData("LastSeenUtc", "last_seen_utc")]
    [InlineData("HttpPath", "http_path")]
    [InlineData("Sha256X64", "sha256x64")]
    [InlineData("Id", "id")]
    [InlineData("AgentInterfaces", "agent_interfaces")]
    public void Snake_case_conversion(string input, string expected) => Assert.Equal(expected, NmsDbContext.ToSnake(input));
}
