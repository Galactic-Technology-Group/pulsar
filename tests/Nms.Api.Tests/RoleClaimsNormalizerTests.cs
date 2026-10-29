using System.Security.Claims;
using Nms.Api.Auth;

namespace Nms.Api.Tests;

public class RoleClaimsNormalizerTests
{
    [Theory]
    [InlineData("nms.admin", "Nms.Admin")]
    [InlineData("NMS.OPERATOR", "Nms.Operator")]
    [InlineData("Nms.Viewer", "Nms.Viewer")]
    public async Task Maps_any_casing_to_canonical_role(string given, string expected)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, given)], "test");
        var principal = await new RoleClaimsNormalizer().TransformAsync(new ClaimsPrincipal(identity));
        Assert.True(principal.IsInRole(expected));
        Assert.Single(principal.FindAll(ClaimTypes.Role));
    }

    [Fact]
    public async Task Leaves_unknown_roles_alone()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, "Something.Else")], "test");
        var principal = await new RoleClaimsNormalizer().TransformAsync(new ClaimsPrincipal(identity));
        Assert.True(principal.IsInRole("Something.Else"));
    }
}
