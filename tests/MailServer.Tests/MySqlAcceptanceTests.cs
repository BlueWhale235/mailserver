using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MailServer.Tests;

public sealed class MySqlAcceptanceTests
{
    [Fact]
    public async Task MySqlMigrationVersionContentionAndTwoWorkerLeases()
    {
        var connection = Environment.GetEnvironmentVariable("MAILSERVER_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return; // Opt-in: the wrapper script requires this value.
        if (!connection.Contains("mailserver_acceptance", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MySQL acceptance tests require an isolated mailserver_acceptance database.");
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-mysql-acceptance-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "MySql",
            ["ConnectionStrings:MailDatabase"] = connection,
            ["Storage:RootPath"] = Path.Combine(root, "messages"),
            ["SecurityAssets:RootPath"] = Path.Combine(root, "security")
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMailServerInfrastructure(configuration, "worker");
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<MailDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            Assert.True(await db.ConfigurationVersions.AnyAsync(x => x.Scope == ConfigurationScopes.Routing));
        }
        long before;
        await using (var db = await factory.CreateDbContextAsync())
            before = await db.ConfigurationVersions.Where(x => x.Scope == ConfigurationScopes.Rules)
                .Select(x => x.Version).SingleAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = await factory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Rules, CancellationToken.None);
            await transaction.CommitAsync();
        }));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(before + 8, await db.ConfigurationVersions.Where(x => x.Scope == ConfigurationScopes.Rules)
                .Select(x => x.Version).SingleAsync());
            var message = new StoredMessage { ContentKey = Guid.NewGuid().ToString("N"), EnvelopeFrom = "sender@example.test" };
            db.Messages.Add(message);
            for (var index = 0; index < 4; index++)
                db.DeliveryJobs.Add(new DeliveryJob
                {
                    Message = message, Sender = "sender@example.test",
                    Recipient = $"recipient{index}@outside.test",
                    NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                });
            await db.SaveChangesAsync();
        }
        var queue = provider.GetRequiredService<IDeliveryQueue>();
        var first = queue.LeaseAsync("acceptance-a", 2, TimeSpan.FromMinutes(1), CancellationToken.None);
        var second = queue.LeaseAsync("acceptance-b", 2, TimeSpan.FromMinutes(1), CancellationToken.None);
        await Task.WhenAll(first, second);
        var leased = (await first).Concat(await second).ToArray();
        Assert.Equal(4, leased.Length);
        Assert.Equal(4, leased.Select(x => x.Id).Distinct().Count());
    }
}
