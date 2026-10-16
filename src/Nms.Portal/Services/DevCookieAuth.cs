using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Nms.Portal.Services;

/// <summary>Local development only: everyone is "dev" with all roles. Enabled when Auth:Mode == "Development".</summary>
public sealed class DevCookieAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Dev";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "dev"),
            new Claim("preferred_username", "dev@localhost"),
            new Claim(ClaimTypes.Role, Roles.Admin), new Claim(ClaimTypes.Role, Roles.Operator), new Claim(ClaimTypes.Role, Roles.Viewer)
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), SchemeName)));
    }
}

public static class Roles
{
    public const string Admin = "Nms.Admin";
    public const string Operator = "Nms.Operator";
    public const string Viewer = "Nms.Viewer";
}
