using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
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

if (devAuth)
{
    builder.Services.AddAuthentication(DevCookieAuth.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevCookieAuth>(DevCookieAuth.SchemeName, null);
    builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
}
else
{
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
        .EnableTokenAcquisitionToCallDownstreamApi(apiScope is null ? [] : [apiScope])
        .AddInMemoryTokenCaches();
    builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();
}

builder.Services.AddTransient<ApiAuthHandler>();
builder.Services.AddHttpClient<NmsApiClient>(c => c.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/"))
    .AddHttpMessageHandler<ApiAuthHandler>();
builder.Services.AddScoped<LiveUpdates>();

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
