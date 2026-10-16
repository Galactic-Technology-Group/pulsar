using Nms.Agent;
using Nms.Agent.Network;
using Nms.Agent.Probes;
using Nms.Agent.Scheduling;
using Nms.Agent.Storage;
using Nms.Agent.Transport;

var builder = Host.CreateApplicationBuilder(args);

// Config precedence: appsettings.json < /etc/nms-agent/agent.json (or NMS_CONFIG) < NMS_* env vars < args
var cfgPath = Environment.GetEnvironmentVariable("NMS_CONFIG") ?? AgentOptions.DefaultConfigPath;
builder.Configuration.AddJsonFile(cfgPath, optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("NMS_");
builder.Configuration.AddCommandLine(args);

builder.Services.AddSystemd();
builder.Services.Configure<AgentOptions>(builder.Configuration);

builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<InterfaceDiscovery>();
builder.Services.AddSingleton<ResultOutbox>();
builder.Services.AddSingleton<ApiClient>();
builder.Services.AddSingleton<EnrollmentService>();
builder.Services.AddSingleton<SelfUpdater>();
builder.Services.AddSingleton<DiscoveryService>();
builder.Services.AddSingleton<IProbe, IcmpProbe>();
builder.Services.AddSingleton<IProbe, TcpProbe>();
builder.Services.AddSingleton<IProbe, HttpProbe>();
builder.Services.AddSingleton<CheckScheduler>();

builder.Services.AddHostedService<HubConnectionService>();
builder.Services.AddHostedService<ResultsUploader>();

var host = builder.Build();
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("nms-agent")
    .LogInformation("nms-agent {Version} starting; config {Config}", AgentVersion.Current, cfgPath);
host.Run();
