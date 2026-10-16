using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Nms.Api.Auth;

/// <summary>Local development only: every request is an Admin named "dev". Enabled when Auth:Mode == "Development".</summary>
public sealed class DevAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Dev";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "dev"),
            new Claim(ClaimTypes.NameIdentifier, "dev"),
            new Claim(ClaimTypes.Role, Policies.Admin),
            new Claim(ClaimTypes.Role, Policies.Operator),
            new Claim(ClaimTypes.Role, Policies.Viewer),
        }, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public static class Policies
{
    // Entra app roles must be named exactly like this.
    public const string Admin = "Nms.Admin";
    public const string Operator = "Nms.Operator";
    public const string Viewer = "Nms.Viewer";
    public const string User = "User";
}
