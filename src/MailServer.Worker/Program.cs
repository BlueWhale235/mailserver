using MailServer.Infrastructure;
using MailServer.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddPortableConfiguration();
builder.Logging.AddMailServerLogging(builder.Configuration);
builder.Services.AddMailServerInfrastructure(builder.Configuration);
builder.Services.AddMailServerPlugins(builder.Configuration, "worker");
builder.Services.AddSingleton<BounceService>();
builder.Services.AddHostedService<DeliveryWorker>();
builder.Services.AddHostedService<SentCopyBackgroundService>();
builder.Services.AddHostedService<TlsReportBackgroundService>();

var host = builder.Build();
await host.Services.VerifyMailDatabaseAsync();
await host.RunAsync();
