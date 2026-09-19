using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddPortableConfiguration();
builder.Logging.AddMailServerLogging(builder.Configuration);
builder.Services.AddMailServerInfrastructure(builder.Configuration);
builder.Services.AddClusterTransportHost();
builder.Services.AddHostedService<ClusterHostGuard>();

var host = builder.Build();
await host.Services.VerifyMailDatabaseAsync();
await host.RunAsync();

internal sealed class ClusterHostGuard(
    IOptions<ClusterOptions> cluster,
    IOptions<ClusterTransportOptions> transport,
    IDbContextFactory<MailDbContext> factory,
    ILogger<ClusterHostGuard> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = ClusterHostValidator.ValidateWitnessHost(cluster.Value, transport.Value);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Cluster host configuration is invalid: " + string.Join("; ", errors));
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var configuration = await db.ClusterConfigurations.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var localNode = await db.ClusterNodes.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == cluster.Value.NodeId && x.RevokedAt == null, cancellationToken);
        var primaryExists = configuration is not null && await db.ClusterNodes.AsNoTracking()
            .AnyAsync(x => x.Id == configuration.PrimaryNodeId && x.Role == ClusterNodeRole.Primary && x.RevokedAt == null, cancellationToken);
        var localCertificateExists = localNode?.CertificateThumbprint is not null
            && await db.ClusterNodeCertificates.AsNoTracking().AnyAsync(x =>
                x.NodeId == cluster.Value.NodeId
                && x.Thumbprint == localNode.CertificateThumbprint
                && x.RevokedAt == null,
                cancellationToken);
        if (configuration is null || localNode is null || !primaryExists || !localCertificateExists)
            throw new InvalidOperationException(
                "The local cluster identity database is incomplete. Remove this node on the primary, create a new invitation, and run 'mailserver cluster join' again.");

        logger.LogInformation(
            "Cluster host started as {Role} node {NodeId}; listening on {Address}:{Port}.",
            cluster.Value.Role,
            cluster.Value.NodeId,
            transport.Value.ListenAddress,
            transport.Value.Port);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
