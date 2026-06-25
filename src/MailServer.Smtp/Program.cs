using MailServer.Infrastructure;
using MailServer.Smtp;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddPortableConfiguration();
builder.Services.AddMailServerInfrastructure(builder.Configuration);
builder.Services.AddMailServerPlugins(builder.Configuration, "smtp");
builder.Services.Configure<SmtpServerOptions>(builder.Configuration.GetSection(SmtpServerOptions.SectionName));
builder.Services.AddHostedService<SmtpServerService>();

var host = builder.Build();
await host.Services.VerifyMailDatabaseAsync();
await host.RunAsync();
