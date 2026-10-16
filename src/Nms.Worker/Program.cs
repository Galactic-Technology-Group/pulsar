using Nms.Data;
using Nms.Worker;
using Nms.Worker.Notifications;
using Nms.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNmsData(builder.Configuration.GetConnectionString("Nms")
    ?? throw new InvalidOperationException("ConnectionStrings:Nms is required"));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddHttpClient("teams", c => c.Timeout = TimeSpan.FromSeconds(15));

builder.Services.AddSingleton<AlertRouter>();
builder.Services.AddSingleton<ISecretResolver, ConfigurationSecretResolver>();
builder.Services.AddSingleton<INotificationSender, EmailSender>();
builder.Services.AddSingleton<INotificationSender, TeamsWebhookSender>();

builder.Services.AddHostedService<NotificationDispatcher>();
builder.Services.AddHostedService<AgentWatchdog>();
builder.Services.AddHostedService<RetentionJob>();

var host = builder.Build();
host.Run();
