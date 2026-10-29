using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace Nms.Portal.Services;

/// <summary>
/// Entra app roles are matched case-sensitively by <see cref="ClaimsPrincipal.IsInRole"/>. Tenants often
/// define them as <c>nms.admin</c>; this maps any casing onto the canonical <c>Nms.*</c> names.
/// </summary>
public sealed class RoleClaimsNormalizer : IClaimsTransformation
{
    private static readonly string[] Canonical = ["Nms.Admin", "Nms.Operator", "Nms.Viewer"];

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        foreach (var identity in principal.Identities.OfType<ClaimsIdentity>())
        {
            foreach (var claim in identity.FindAll(identity.RoleClaimType).ToList())
            {
                var canonical = Canonical.FirstOrDefault(c => string.Equals(c, claim.Value, StringComparison.OrdinalIgnoreCase));
                if (canonical is null || canonical == claim.Value) continue;
                identity.RemoveClaim(claim);
                identity.AddClaim(new Claim(identity.RoleClaimType, canonical, claim.ValueType, claim.Issuer));
            }
        }
        return Task.FromResult(principal);
    }
}
