using MailServer.Infrastructure;
using MailServer.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddPortableConfiguration();
using var snowflakeLease = SnowflakeProcess.Acquire(builder.Configuration, "worker");
builder.Logging.AddMailServerLogging(builder.Configuration);
builder.Services.AddMailServerInfrastructure(builder.Configuration, "worker");
builder.Services.AddMailServerPlugins(builder.Configuration, "worker");
builder.Services.AddSingleton<BounceService>();
builder.Services.AddHostedService<DeliveryWorker>();

var host = builder.Build();
await host.Services.VerifyMailDatabaseAsync();
await host.RunAsync();
