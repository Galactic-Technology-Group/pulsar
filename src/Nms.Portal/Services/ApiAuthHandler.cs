using System.Net.Http.Headers;
using Microsoft.Identity.Web;

namespace Nms.Portal.Services;

/// <summary>Attaches the signed-in user's access token for the API (no-op in dev mode where the API trusts everyone).</summary>
public sealed class ApiAuthHandler(IServiceProvider services, IConfiguration config) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (config["Auth:Mode"] != "Development" && config["Api:Scope"] is { Length: > 0 } scope)
        {
            var acquisition = services.GetRequiredService<ITokenAcquisition>();
            var token = await acquisition.GetAccessTokenForUserAsync([scope]);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await base.SendAsync(request, ct);
    }
}
