using MailServer.Infrastructure;
using MailServer.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddPortableConfiguration();
builder.Services.AddMailServerInfrastructure(builder.Configuration);
builder.Services.AddMailServerPlugins(builder.Configuration, "worker");
builder.Services.AddSingleton<BounceService>();
builder.Services.AddHostedService<DeliveryWorker>();

var host = builder.Build();
await host.Services.VerifyMailDatabaseAsync();
await host.RunAsync();
