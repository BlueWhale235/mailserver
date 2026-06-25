using MailServer.Imap;
using MailServer.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddPortableConfiguration();
builder.Services.AddMailServerInfrastructure(builder.Configuration);
builder.Services.AddMailServerPlugins(builder.Configuration, "imap");
builder.Services.Configure<ImapServerOptions>(builder.Configuration.GetSection(ImapServerOptions.SectionName));
builder.Services.AddHostedService<ImapServerService>();

var host = builder.Build();
await host.Services.VerifyMailDatabaseAsync();
await host.RunAsync();
