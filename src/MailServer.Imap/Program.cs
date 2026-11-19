using MailServer.Imap;
using MailServer.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddPortableConfiguration();
using var snowflakeLease = SnowflakeProcess.Acquire(builder.Configuration, "imap");
builder.Logging.AddMailServerLogging(builder.Configuration);
builder.Services.AddMailServerInfrastructure(builder.Configuration, "imap");
builder.Services.AddMailServerPlugins(builder.Configuration, "imap");
builder.Services.Configure<ImapServerOptions>(builder.Configuration.GetSection(ImapServerOptions.SectionName));
builder.Services.AddHostedService<ImapServerService>();

var host = builder.Build();
await host.Services.VerifyMailDatabaseAsync();
await host.RunAsync();
