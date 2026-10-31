using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Nms.Portal.Components;
using Nms.Portal.Services;

var builder = WebApplication.CreateBuilder(args);

var devAuth = builder.Configuration["Auth:Mode"] == "Development";
var apiBase = builder.Configuration["Api:BaseUrl"] ?? throw new InvalidOperationException("Api:BaseUrl is required");
var apiScope = builder.Configuration["Api:Scope"];

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

// Persist the Data Protection key ring outside the container so the auth cookie survives restarts
// and is valid on every replica. Without this each replica mints its own ephemeral keys and users
// are signed out on every deploy. Configured by infra (DataProtection:BlobUri / KeyId); absent locally.
var dp = builder.Services.AddDataProtection().SetApplicationName("pulsar-portal");
if (builder.Configuration["DataProtection:BlobUri"] is { Length: > 0 } blobUri)
{
    TokenCredential credential = builder.Configuration["AZURE_CLIENT_ID"] is { Length: > 0 } uami
        ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(uami))
        : new DefaultAzureCredential();
    dp.PersistKeysToAzureBlobStorage(new Uri(blobUri), credential);
    if (builder.Configuration["DataProtection:KeyId"] is { Length: > 0 } keyId)
        dp.ProtectKeysWithAzureKeyVault(new Uri(keyId), credential);
}

if (devAuth)
{
    builder.Services.AddAuthentication(DevCookieAuth.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevCookieAuth>(DevCookieAuth.SchemeName, null);
    builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
}
else
{
    builder.Services.AddScoped<IClaimsTransformation, ApiRoleClaimsTransformation>();
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
        .EnableTokenAcquisitionToCallDownstreamApi(apiScope is null ? [] : [apiScope])
        .AddInMemoryTokenCaches();
    // The token cache is per-process. After a restart the cookie is still valid (key ring above) but the
    // access token is gone; NmsApiClient hands the resulting challenge exception to this handler, which
    // sends the browser through a silent Entra round-trip instead of showing an error.
    builder.Services.AddMicrosoftIdentityConsentHandler();
    builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();
}

builder.Services.AddTransient<ApiAuthHandler>();
builder.Services.AddHttpClient<NmsApiClient>(c => c.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/"))
    .AddHttpMessageHandler<ApiAuthHandler>();
builder.Services.AddScoped<LiveUpdates>();

builder.Services.AddHealthChecks();

var app = builder.Build();

if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Error", createScopeForErrors: true); app.UseHsts(); }
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

if (!devAuth) app.MapControllers();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapHealthChecks("/healthz").AllowAnonymous();

app.Run();
