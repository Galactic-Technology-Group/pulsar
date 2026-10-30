using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Nms.Portal.Services;

/// <summary>
/// Entra puts app roles in the token issued <em>for the app that defines them</em>. Roles live on the API
/// registration, so the portal's own ID token carries none. This copies the <c>roles</c> claim from the
/// API access token (already acquired for API calls) onto the signed-in principal and normalizes casing
/// to the canonical <c>Nms.*</c> names, so <c>AuthorizeView</c> in the portal agrees with the API.
/// </summary>
public sealed class ApiRoleClaimsTransformation(ITokenAcquisition tokens, IConfiguration config, ILogger<ApiRoleClaimsTransformation> log)
    : IClaimsTransformation
{
    private const string SyncedMarker = "nms:api-roles-synced";
    private static readonly string[] Canonical = ["Nms.Admin", "Nms.Operator", "Nms.Viewer"];

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated) return principal;
        if (identity.HasClaim(c => c.Type == SyncedMarker)) return principal;

        var roleType = identity.RoleClaimType;
        if (config["Api:Scope"] is { Length: > 0 } scope)
        {
            try
            {
                var token = await tokens.GetAccessTokenForUserAsync([scope], user: principal);
                var apiRoles = new JsonWebToken(token).Claims.Where(c => c.Type == "roles").Select(c => c.Value);
                foreach (var role in apiRoles)
                    if (!identity.HasClaim(roleType, role)) identity.AddClaim(new Claim(roleType, role));
            }
            catch (MsalUiRequiredException)
            {
                // Token cache is in-memory; after a restart the user must sign in again. Leave the principal as is.
                return principal;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not read roles from the API access token");
                return principal;
            }
        }

        foreach (var claim in identity.FindAll(roleType).ToList())
        {
            var canonical = Canonical.FirstOrDefault(c => string.Equals(c, claim.Value, StringComparison.OrdinalIgnoreCase));
            if (canonical is null || canonical == claim.Value) continue;
            identity.RemoveClaim(claim);
            identity.AddClaim(new Claim(roleType, canonical, claim.ValueType, claim.Issuer));
        }
        identity.AddClaim(new Claim(SyncedMarker, "1"));
        return principal;
    }
}
