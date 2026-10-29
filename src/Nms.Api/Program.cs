using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Nms.Api.Auth;
using Nms.Api.Endpoints;
using Nms.Api.Hubs;
using Nms.Api.Services;
using Nms.Data;
using Nms.Shared.Contracts;
using Nms.Shared.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddNmsData(builder.Configuration.GetConnectionString("Nms")
    ?? throw new InvalidOperationException("ConnectionStrings:Nms is required"));
builder.Services.AddMemoryCache();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 1024 * 1024)
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ---- Authentication: agents (header secret) + humans (Entra ID JWT, or Dev scheme locally) ----
var devAuth = builder.Configuration["Auth:Mode"] == "Development";
var auth = builder.Services.AddAuthentication(devAuth ? DevAuthenticationHandler.SchemeName : JwtBearerDefaults.AuthenticationScheme);
auth.AddScheme<AuthenticationSchemeOptions, AgentAuthenticationHandler>(AgentAuth.Scheme, null);
if (devAuth)
    auth.AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(DevAuthenticationHandler.SchemeName, null);
else
    auth.AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddSingleton<IClaimsTransformation, RoleClaimsNormalizer>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AgentAuth.Policy, p => p.AddAuthenticationSchemes(AgentAuth.Scheme).RequireAuthenticatedUser().RequireRole("Agent"))
    .AddPolicy(Policies.User, p => p
        .AddAuthenticationSchemes(devAuth ? DevAuthenticationHandler.SchemeName : JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser().RequireRole(Policies.Admin, Policies.Operator, Policies.Viewer));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("agent", ctx => RateLimitPartition.GetTokenBucketLimiter(
        ctx.Request.Headers[AgentAuth.AgentIdHeader].ToString() is { Length: > 0 } id ? id : ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new TokenBucketRateLimiterOptions { TokenLimit = 60, TokensPerPeriod = 30, ReplenishmentPeriod = TimeSpan.FromSeconds(10), AutoReplenishment = true }));
});

builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<DiscoveryStore>();
builder.Services.AddScoped<AgentConfigBuilder>();
builder.Services.AddScoped<ResultIngestService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/healthz").AllowAnonymous();
app.MapAgentEndpoints();
app.MapManagementEndpoints();
app.MapHub<AgentHub>(HubRoutes.Agent);
app.MapHub<PortalHub>(HubRoutes.Portal);

if (!app.Configuration.GetValue<bool>("Database:SkipInitialize"))
    await DbInitializer.InitializeAsync(app.Services);

app.Run();

public partial class Program { }
