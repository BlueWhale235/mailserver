using MailServer.Infrastructure;
using MailServer.Smtp;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddPortableConfiguration();
builder.Logging.AddMailServerLogging(builder.Configuration);
builder.Services.AddMailServerInfrastructure(builder.Configuration, "smtp");
builder.Services.AddMailServerPlugins(builder.Configuration, "smtp");
builder.Services.Configure<SmtpServerOptions>(builder.Configuration.GetSection(SmtpServerOptions.SectionName));
builder.Services.Configure<SmtpAbuseOptions>(builder.Configuration.GetSection(SmtpAbuseOptions.SectionName));
builder.Services.AddHostedService<SmtpServerService>();
builder.Services.AddHostedService<SentCopyBackgroundService>();

var host = builder.Build();
await host.Services.VerifyMailDatabaseAsync();
await host.RunAsync();
