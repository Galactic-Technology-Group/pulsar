using Nms.Api.Endpoints;
using Nms.Shared.Contracts;

namespace Nms.Api.Tests;

public class PushApiTests
{
    private static readonly string Token = new('a', 64);

    [Theory]
    [InlineData("production")]
    [InlineData("sandbox")]
    [InlineData(" Sandbox ")]
    public void A_hex_token_with_a_known_environment_is_accepted(string environment) =>
        Assert.Null(PushEndpoints.InputError(new PushDeviceInput(Token, environment, "Chris's iPhone")));

    [Theory]
    [InlineData("")]
    [InlineData("not-hex-not-hex-not-hex-not-hex-not-hex")]
    [InlineData("abc123")]                       // too short to be a device token
    public void Anything_that_is_not_a_device_token_is_refused(string token) =>
        Assert.Contains("Token", PushEndpoints.InputError(new PushDeviceInput(token)));

    [Fact]
    public void An_unknown_environment_is_refused() =>
        Assert.Contains("Environment", PushEndpoints.InputError(new PushDeviceInput(Token, "staging")));
}
