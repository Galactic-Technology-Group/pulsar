using Nms.Agent;
using Nms.Agent.Network;
using Nms.Agent.Probes;
using Nms.Agent.Probes.Snmp;
using Nms.Agent.Scheduling;
using Nms.Agent.Storage;
using Nms.Agent.Transport;

var builder = Host.CreateApplicationBuilder(args);

// Config precedence: appsettings.json < /etc/nms-agent/agent.json (or NMS_CONFIG) < NMS_* env vars < args
var cfgPath = Environment.GetEnvironmentVariable("NMS_CONFIG") ?? AgentOptions.DefaultConfigPath;
builder.Configuration.AddJsonFile(cfgPath, optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("NMS_");
builder.Configuration.AddCommandLine(args);

// Before anything that could crash: count this start if it is an unconfirmed self-update, and roll back after too
// many. Exiting lets systemd (Restart=always) start the restored binary.
var guard = UpdateGuard.ForThisProcess(builder.Configuration["StateDir"] ?? AgentOptions.DefaultStateDir);
if (OperatingSystem.IsLinux() && guard.OnStartup())
{
    Console.Error.WriteLine($"nms-agent {AgentVersion.Current}: unconfirmed update kept failing; previous binary restored, exiting for restart");
    return;
}

builder.Services.AddSystemd();
builder.Services.AddSingleton(guard);
builder.Services.Configure<AgentOptions>(builder.Configuration);

builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<InterfaceDiscovery>();
builder.Services.AddSingleton<ResultOutbox>();
builder.Services.AddSingleton<ApiClient>();
builder.Services.AddSingleton<EnrollmentService>();
builder.Services.AddSingleton<SelfUpdater>();
builder.Services.AddSingleton<IReverseResolver, HostnameResolver>();
builder.Services.AddSingleton<DiscoveryService>();
builder.Services.AddSingleton<IProbe, IcmpProbe>();
builder.Services.AddSingleton<IProbe, TcpProbe>();
builder.Services.AddSingleton<IProbe, HttpProbe>();
builder.Services.AddSingleton<CounterRates>();
builder.Services.AddSingleton<IProbe, SnmpProbe>();
builder.Services.AddSingleton<CheckScheduler>();

builder.Services.AddSingleton<HubConnectionService>();
builder.Services.AddSingleton<IHubStatus>(sp => sp.GetRequiredService<HubConnectionService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<HubConnectionService>());
builder.Services.AddHostedService(sp => new UpdateWatchdog(
    sp.GetRequiredService<UpdateGuard>(), sp.GetRequiredService<IHubStatus>(),
    sp.GetRequiredService<ApiClient>().IsApiReachableAsync,
    sp.GetRequiredService<IHostApplicationLifetime>(), sp.GetRequiredService<ILogger<UpdateWatchdog>>()));
builder.Services.AddHostedService<ResultsUploader>();

var host = builder.Build();
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("nms-agent")
    .LogInformation("nms-agent {Version} starting; config {Config}", AgentVersion.Current, cfgPath);
host.Run();
