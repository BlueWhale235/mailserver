using System.Security.Cryptography;
using System.Security.Authentication;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Security.Claims;
using MailServer.Api;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace MailServer.Tests;

public sealed class DatabaseAndCertificateTests
{
    [Fact]
    public async Task TaskExecutionSearchPagesFiltersAndRejectsActiveLease()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-task-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")}",
                ["Storage:RootPath"] = Path.Combine(root, "messages"),
                ["SecurityAssets:RootPath"] = Path.Combine(root, "security")
            }).Build();
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<IConfiguration>(configuration);
            collection.AddMailServerInfrastructure(configuration);
            await using var services = collection.BuildServiceProvider();
            var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
            var now = DateTimeOffset.UtcNow;
            var first = new ScheduledTask { Type = "test.first", Name = "First" };
            var second = new ScheduledTask { Type = "test.second", Name = "Second" };
            Guid detailId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                db.ScheduledTasks.AddRange(first, second);
                var rows = Enumerable.Range(0, 25).Select(index => new TaskExecution
                {
                    ScheduledTaskId = index < 20 ? first.Id : second.Id,
                    Status = index % 5 == 0 ? TaskExecutionStatus.Failed : TaskExecutionStatus.Succeeded,
                    StartedAt = now.AddMinutes(-index),
                    CompletedAt = now.AddMinutes(-index).AddSeconds(1)
                }).ToArray();
                detailId = rows[24].Id;
                db.TaskExecutions.AddRange(rows);
                db.TaskLeases.Add(new TaskLease { ScheduledTaskId = first.Id, Owner = $"{Environment.MachineName}:{Environment.ProcessId}", ExpiresAt = now.AddMinutes(10) });
                await db.SaveChangesAsync();
            }

            var tasks = services.GetRequiredService<TaskExecutionService>();
            var page1 = await tasks.SearchExecutionsAsync(null, null, null, null, 1, 20, CancellationToken.None);
            var page2 = await tasks.SearchExecutionsAsync(null, null, null, null, 2, 20, CancellationToken.None);
            Assert.Equal(25, page1.TotalCount);
            Assert.Equal(20, page1.Items.Count);
            Assert.Equal(5, page2.Items.Count);
            Assert.Equal(detailId, page2.Items[^1].Id);
            var failed = await tasks.SearchExecutionsAsync(null, TaskExecutionStatus.Failed, null, null, 1, 20, CancellationToken.None);
            Assert.Equal(5, failed.TotalCount);
            var secondOnly = await tasks.SearchExecutionsAsync(second.Id, null, now.AddHours(-1), now, 1, 20, CancellationToken.None);
            Assert.Equal(5, secondOnly.TotalCount);
            Assert.NotNull(await tasks.GetExecutionAsync(detailId, CancellationToken.None));
            Assert.Null(await tasks.GetExecutionAsync(Guid.NewGuid(), CancellationToken.None));
            await Assert.ThrowsAsync<TaskBusyException>(() => tasks.UpdateAsync(first.Id, true, 60, 600, 100, CancellationToken.None));
            await Assert.ThrowsAsync<TaskBusyException>(() => tasks.RunNowAsync(first.Id, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OverviewTrafficUsesStoredMessagesAndDeliveryAttempts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-overview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")}",
                ["Storage:RootPath"] = Path.Combine(root, "messages"),
                ["SecurityAssets:RootPath"] = Path.Combine(root, "security")
            }).Build();
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<IConfiguration>(configuration);
            collection.AddMailServerInfrastructure(configuration);
            await using var services = collection.BuildServiceProvider();
            var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
            var now = DateTimeOffset.UtcNow;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                var message = new StoredMessage
                {
                    ContentKey = "overview-test-message",
                    EnvelopeFrom = "sender@example.test",
                    Size = 100,
                    ReceivedAt = now.AddHours(-1)
                };
                var oldMessage = new StoredMessage
                {
                    ContentKey = "overview-old-message",
                    EnvelopeFrom = "sender@example.test",
                    Size = 100,
                    ReceivedAt = now.AddDays(-8)
                };
                var pending = new DeliveryJob
                {
                    Message = message,
                    Sender = "sender@example.test",
                    Recipient = "recipient@example.test",
                    CreatedAt = now.AddHours(-2)
                };
                db.Messages.AddRange(message, oldMessage);
                db.DeliveryJobs.Add(pending);
                db.DeliveryAttempts.AddRange(
                    new DeliveryAttempt { DeliveryJob = pending, Succeeded = true, DurationMilliseconds = 120, CreatedAt = now.AddMinutes(-30) },
                    new DeliveryAttempt { DeliveryJob = pending, PermanentFailure = false, DurationMilliseconds = 70, CreatedAt = now.AddMinutes(-20) },
                    new DeliveryAttempt { DeliveryJob = pending, PermanentFailure = true, DurationMilliseconds = 80, CreatedAt = now.AddMinutes(-10) },
                    new DeliveryAttempt { DeliveryJob = pending, Succeeded = true, DurationMilliseconds = 900, CreatedAt = now.AddDays(-8) });
                await db.SaveChangesAsync();
            }

            var administration = services.GetRequiredService<AdminApplicationService>();
            var traffic = await administration.GetOverviewTrafficAsync(1, CancellationToken.None);
            Assert.Equal(1, traffic.Days);
            Assert.Equal(6, traffic.Buckets.Count);
            Assert.Equal(1, traffic.Buckets.Sum(bucket => bucket.StoredMessages));
            Assert.Equal(1, traffic.Buckets.Sum(bucket => bucket.Delivered));
            Assert.Equal(1, traffic.Buckets.Sum(bucket => bucket.TemporaryFailures));
            Assert.Equal(1, traffic.Buckets.Sum(bucket => bucket.PermanentFailures));
            Assert.Equal(120, traffic.P95DeliveryMilliseconds);
            var dashboard = await administration.GetDashboardAsync(CancellationToken.None);
            Assert.Equal(now.AddHours(-2), dashboard.OldestPendingDeliveryAt);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => administration.GetOverviewTrafficAsync(30, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PermissionDelegationCannotExceedActorCapabilitiesOrDomainScope()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-delegation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")}",
                ["Storage:RootPath"] = Path.Combine(root, "messages")
            }).Build();
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<IConfiguration>(configuration);
            collection.AddMailServerInfrastructure(configuration);
            await using var services = collection.BuildServiceProvider();
            var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
            var primary = new AdminUser { UserName = "primary", PasswordHash = "test", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
            var limited = new AdminUser { UserName = "limited", PasswordHash = "test" };
            var allowed = new MailDomain { Name = "allowed.example" };
            var hidden = new MailDomain { Name = "hidden.example" };
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                db.AdminUsers.AddRange(primary, limited);
                db.Domains.AddRange(allowed, hidden);
                foreach (var code in new[] { PermissionCodes.IamManage, PermissionCodes.AdminAccountManage, PermissionCodes.ApiKeyManage })
                {
                    db.PermissionGrants.Add(new PermissionGrant
                    {
                        SubjectType = PermissionSubjectType.AdminUser,
                        SubjectId = limited.Id,
                        PermissionCode = code
                    });
                }
                db.PermissionGrants.Add(new PermissionGrant
                {
                    SubjectType = PermissionSubjectType.AdminUser,
                    SubjectId = limited.Id,
                    PermissionCode = PermissionCodes.DomainRead,
                    ResourceType = PermissionService.DomainResourceType,
                    ResourceKey = allowed.Id.ToString()
                });
                await db.SaveChangesAsync();
            }

            var permissions = services.GetRequiredService<PermissionService>();
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, limited.Id.ToString())], "test"));
            Assert.True(await permissions.CanDelegateGrantsAsync(principal,
                [new PermissionGrantInput(PermissionCodes.DomainRead, "domain", allowed.Id.ToString())], CancellationToken.None));
            Assert.False(await permissions.CanDelegateGrantsAsync(principal,
                [new PermissionGrantInput(PermissionCodes.DomainRead, "domain", hidden.Id.ToString())], CancellationToken.None));
            Assert.False(await permissions.CanDelegateGrantsAsync(principal,
                [new PermissionGrantInput(PermissionCodes.DomainRead, "domain", "*")], CancellationToken.None));
            Assert.False(await permissions.CanDelegateGrantsAsync(principal,
                [new PermissionGrantInput(PermissionCodes.DomainManage, "domain", allowed.Id.ToString())], CancellationToken.None));
            Assert.False(await permissions.CanDelegateGrantsAsync(principal,
                [new PermissionGrantInput(PermissionCodes.QueueManage)], CancellationToken.None));
            Assert.False(await permissions.CanDelegateGrantsAsync(principal,
                [new PermissionGrantInput(PermissionCodes.FullAccess)], CancellationToken.None));
            Assert.True(await permissions.CanDelegateGrantsAsync(principal,
                [new PermissionGrantInput(PermissionCodes.AdminAccountRead)], CancellationToken.None));
            var quarantineScope = await permissions.GetPermittedDomainScopeAsync(
                principal, PermissionCodes.DomainQuarantineRead, CancellationToken.None);
            Assert.False(quarantineScope.AllDomains);
            Assert.Contains(allowed.Id, quarantineScope.DomainIds);
            Assert.DoesNotContain(hidden.Id, quarantineScope.DomainIds);
            Assert.True(await permissions.CanDelegateGrantsAsync(principal,
                [new PermissionGrantInput(PermissionCodes.DomainQuarantineRead, "domain", allowed.Id.ToString())], CancellationToken.None));

            var primaryPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, primary.Id.ToString())], "test"));
            Assert.True(await permissions.CanDelegateGrantsAsync(primaryPrincipal,
                [new PermissionGrantInput(PermissionCodes.FullAccess)], CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PermissionDomainOptionsPageBeyondOneHundredAndRespectScope()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-permission-domains-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")}",
                ["Storage:RootPath"] = Path.Combine(root, "messages")
            }).Build();
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<IConfiguration>(configuration);
            collection.AddMailServerInfrastructure(configuration);
            await using var services = collection.BuildServiceProvider();
            var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
            Guid hiddenId;
            Guid allowedId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                var domains = Enumerable.Range(1, 125)
                    .Select(index => new MailDomain { Name = $"domain-{index:D3}.example" }).ToArray();
                db.Domains.AddRange(domains);
                await db.SaveChangesAsync();
                allowedId = domains[124].Id;
                hiddenId = domains[0].Id;
            }

            var administration = services.GetRequiredService<AdminApplicationService>();
            var all = new PermittedDomainScope(true, new HashSet<Guid>());
            var last = await administration.SearchPermissionDomainsAsync(null, 7, 20, all, CancellationToken.None);
            Assert.Equal(125, last.TotalCount);
            Assert.Equal(5, last.Items.Count);
            Assert.Contains(last.Items, item => item.Id == allowedId);
            Assert.Equal(1, (await administration.SearchPermissionDomainsAsync(
                "DOMAIN-125", 1, 20, all, CancellationToken.None)).TotalCount);

            var restricted = new PermittedDomainScope(false, new HashSet<Guid> { allowedId });
            var visible = await administration.SearchPermissionDomainsAsync(null, 1, 20, restricted, CancellationToken.None);
            Assert.Single(visible.Items);
            Assert.Equal(allowedId, visible.Items[0].Id);
            var resolved = await administration.ResolvePermissionDomainsAsync(
                [allowedId, hiddenId], restricted, CancellationToken.None);
            Assert.Single(resolved);
            Assert.Equal(allowedId, resolved[0].Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApiKeySearchPaginatesAndFiltersPersistedKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-api-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")}",
                ["Storage:RootPath"] = Path.Combine(root, "messages")
            }).Build();
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<IConfiguration>(configuration);
            collection.AddMailServerInfrastructure(configuration);
            await using var services = collection.BuildServiceProvider();
            var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
            var createdAt = DateTimeOffset.UtcNow.AddDays(-1);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                for (var index = 0; index < 7; index++)
                {
                    db.ApiKeys.Add(new ApiKeyRecord
                    {
                        Name = $"Agent-{index}", Prefix = $"msk_{index}", SecretHash = "test-only",
                        CreatedBy = "Ops-Team", CreatedAt = createdAt,
                        ExpiresAt = index == 5 ? createdAt : null,
                        RevokedAt = index == 6 ? createdAt : null
                    });
                }
                await db.SaveChangesAsync();
            }

            var administration = services.GetRequiredService<AdminApplicationService>();
            var first = await administration.SearchApiKeysAsync(null, ApiKeyStatusFilter.Any, 1, 5, CancellationToken.None);
            var second = await administration.SearchApiKeysAsync(null, ApiKeyStatusFilter.Any, 2, 5, CancellationToken.None);
            Assert.Equal(7, first.TotalCount);
            Assert.Equal(5, first.Items.Count);
            Assert.Equal(2, second.Items.Count);
            Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
            Assert.Equal(5, (await administration.SearchApiKeysAsync(null, ApiKeyStatusFilter.Active, 1, 20, CancellationToken.None)).TotalCount);
            Assert.Equal(1, (await administration.SearchApiKeysAsync(null, ApiKeyStatusFilter.Expired, 1, 20, CancellationToken.None)).TotalCount);
            Assert.Equal(1, (await administration.SearchApiKeysAsync(null, ApiKeyStatusFilter.Revoked, 1, 20, CancellationToken.None)).TotalCount);
            Assert.Equal(7, (await administration.SearchApiKeysAsync("OPS-TEAM", ApiKeyStatusFilter.Any, 1, 20, CancellationToken.None)).TotalCount);
            Assert.Equal(1, (await administration.SearchApiKeysAsync("agent-3", ApiKeyStatusFilter.Any, 1, 20, CancellationToken.None)).TotalCount);
            Assert.Empty((await administration.SearchApiKeysAsync(null, ApiKeyStatusFilter.Any, 3, 5, CancellationToken.None)).Items);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadinessReflectsStorageAndPendingMigrationsWithoutExposingPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-readiness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var storage = Path.Combine(root, "messages");
            var security = Path.Combine(root, "security");
            var blocked = Path.Combine(root, "not-a-directory");
            File.WriteAllText(blocked, "blocked");
            var settings = new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")}",
                ["Storage:RootPath"] = storage,
                ["SecurityAssets:RootPath"] = security
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMailServerInfrastructure(configuration);
            {
                await using var provider = services.BuildServiceProvider();
                var factory = provider.GetRequiredService<IDbContextFactory<MailDbContext>>();
                await using (var db = await factory.CreateDbContextAsync())
                    await db.Database.MigrateAsync();

                var healthy = await MailServerReadiness.CheckAsync(factory, configuration, CancellationToken.None);
                Assert.Equal("ready", healthy.Status);
                Assert.Equal("ready", healthy.Database);
                Assert.Equal("ready", healthy.MessageStorage);
                Assert.Equal("ready", healthy.SecurityAssets);

                settings["Storage:RootPath"] = blocked;
                var blockedStorage = await MailServerReadiness.CheckAsync(factory,
                    new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), CancellationToken.None);
                Assert.Equal("unavailable", blockedStorage.Status);
                Assert.Equal("unavailable", blockedStorage.MessageStorage);
                Assert.DoesNotContain(root, System.Text.Json.JsonSerializer.Serialize(blockedStorage));

                settings["Storage:RootPath"] = storage;
                await using (var db = await factory.CreateDbContextAsync())
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\"");
                var outdated = await MailServerReadiness.CheckAsync(factory, configuration, CancellationToken.None);
                Assert.Equal("unavailable", outdated.Status);
                Assert.Equal("migration-pending", outdated.Database);
            }

            settings["ConnectionStrings:MailDatabase"] =
                $"Data Source={Path.Combine(root, "missing-parent", "mailserver.db")}";
            var unavailableConfiguration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var unavailableServices = new ServiceCollection();
            unavailableServices.AddLogging();
            unavailableServices.AddMailServerInfrastructure(unavailableConfiguration);
            await using (var unavailableProvider = unavailableServices.BuildServiceProvider())
            {
                var unavailableFactory = unavailableProvider.GetRequiredService<IDbContextFactory<MailDbContext>>();
                var unavailable = await MailServerReadiness.CheckAsync(
                    unavailableFactory, unavailableConfiguration, CancellationToken.None);
                Assert.Equal("unavailable", unavailable.Status);
                Assert.Equal("unavailable", unavailable.Database);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteMigrationsAndQueueLeaseWorkEndToEnd()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-sqlite-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")}",
                    ["Storage:RootPath"] = Path.Combine(root, "messages")
                })
                .Build();
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<IConfiguration>(configuration);
            collection.AddMailServerInfrastructure(configuration);
            await using var services = collection.BuildServiceProvider();
            var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                var message = new StoredMessage
                {
                    ContentKey = new string('a', 64),
                    EnvelopeFrom = "sender@example.com",
                    Size = 42
                };
                db.Messages.Add(message);
                db.DeliveryJobs.Add(new DeliveryJob
                {
                    Message = message,
                    Sender = "sender@example.com",
                    Recipient = "recipient@example.net"
                });
                await db.SaveChangesAsync();
            }

            var queue = services.GetRequiredService<IDeliveryQueue>();
            var leased = await queue.LeaseAsync("test", 1, TimeSpan.FromMinutes(1), CancellationToken.None);

            Assert.Single(leased);
            Assert.Equal(DeliveryStatus.Processing, leased[0].Status);
            await Assert.ThrowsAsync<DeliveryStateConflictException>(() =>
                queue.RetryNowAsync(leased[0].Id, CancellationToken.None));
            await Assert.ThrowsAsync<DeliveryStateConflictException>(() =>
                queue.DeleteAsync(leased[0].Id, CancellationToken.None));

            Guid failedId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var failed = new DeliveryJob
                {
                    MessageId = leased[0].MessageId,
                    Sender = "sender@example.com",
                    Recipient = "other@example.net",
                    Status = DeliveryStatus.Failed
                };
                db.DeliveryJobs.Add(failed);
                await db.SaveChangesAsync();
                failedId = failed.Id;
            }
            var administration = services.GetRequiredService<AdminApplicationService>();
            var retry = await administration.BatchRetryDeliveriesAsync(
                [failedId, leased[0].Id], "test", CancellationToken.None,
                expectedAttempts: new Dictionary<Guid, int> { [failedId] = 0, [leased[0].Id] = 1 });
            Assert.Equal(1, retry.Succeeded);
            Assert.Equal("Conflict", retry.Items[1].Status);
            var repeated = await administration.BatchRetryDeliveriesAsync(
                [failedId], "test", CancellationToken.None,
                expectedAttempts: new Dictionary<Guid, int> { [failedId] = 0 });
            Assert.Equal("Conflict", repeated.Items[0].Status);
            var retriedLease = await queue.LeaseAsync("test-retry", 1, TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.Equal(failedId, Assert.Single(retriedLease).Id);
            await queue.CompleteAsync(failedId, DeliveryResult.Permanent("550 rejected"), CancellationToken.None);
            var staleAfterWorker = await administration.BatchRetryDeliveriesAsync(
                [failedId], "test", CancellationToken.None,
                expectedAttempts: new Dictionary<Guid, int> { [failedId] = 0 });
            Assert.Equal("Conflict", staleAfterWorker.Items[0].Status);
            var freshAfterWorker = await administration.BatchRetryDeliveriesAsync(
                [failedId], "test", CancellationToken.None,
                expectedAttempts: new Dictionary<Guid, int> { [failedId] = 1 });
            Assert.Equal("Succeeded", freshAfterWorker.Items[0].Status);
            await Assert.ThrowsAsync<ArgumentException>(() => administration.BatchRetryDeliveriesAsync(
                [failedId], "test", CancellationToken.None,
                expectedAttempts: new Dictionary<Guid, int>()));
            var deletion = await administration.BatchDeleteDeliveriesAsync(
                [failedId, leased[0].Id], "test", CancellationToken.None);
            Assert.Equal("Succeeded", deletion.Items[0].Status);
            Assert.Equal("Conflict", deletion.Items[1].Status);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.True(await db.DeliveryJobs.AnyAsync(x => x.Id == leased[0].Id));
                Assert.False(await db.DeliveryJobs.AnyAsync(x => x.Id == failedId));
                Assert.Contains(await db.AuditRecords.ToListAsync(), x => x.Action == "queue.batch-delete");
            }
            Guid unreferencedJobId;
            Guid unreferencedMessageId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var message = new StoredMessage
                {
                    ContentKey = new string('b', 64),
                    EnvelopeFrom = "sender@example.com",
                    Size = 42
                };
                var job = new DeliveryJob
                {
                    Message = message,
                    Sender = "sender@example.com",
                    Recipient = "target@example.net"
                };
                db.AddRange(message, job);
                await db.SaveChangesAsync();
                unreferencedJobId = job.Id;
                unreferencedMessageId = message.Id;
            }
            await administration.DeleteDeliveryAsync(unreferencedJobId, "test", CancellationToken.None);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.False(await db.DeliveryJobs.AnyAsync(x => x.Id == unreferencedJobId));
                Assert.False(await db.Messages.AnyAsync(x => x.Id == unreferencedMessageId));
            }
            await queue.CompleteAsync(leased[0].Id, DeliveryResult.Temporary(
                "451 token=hidden-token sender@example.com", diagnostic: new DeliveryDiagnostic("SMTP",
                    SessionSummary: "C: AUTH PLAIN c2VjcmV0\r\nC: RCPT TO:<recipient@example.net>\r\nC: DATA\r\nmail body")),
                CancellationToken.None);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var attempt = await db.DeliveryAttempts.SingleAsync(x => x.DeliveryJobId == leased[0].Id);
                Assert.StartsWith(DeliverySessionSummary.RedactedMarker, attempt.SessionSummary);
                Assert.Contains("[认证会话已省略]", attempt.SessionSummary);
                Assert.DoesNotContain("recipient@example.net", attempt.SessionSummary);
                Assert.DoesNotContain("c2VjcmV0", attempt.SessionSummary);
                Assert.DoesNotContain("mail body", attempt.SessionSummary);
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(attempt.SessionSummary!) <= DeliverySessionSummary.MaximumBytes);
                Assert.DoesNotContain("hidden-token", attempt.Response);
                Assert.DoesNotContain("sender@example.com", attempt.Response);
                var savedJob = await db.DeliveryJobs.SingleAsync(x => x.Id == leased[0].Id);
                Assert.DoesNotContain("hidden-token", savedJob.LastError);
                Assert.DoesNotContain("sender@example.com", savedJob.LastError);
                // Old stored values still need filtering at the admin read boundary.
                attempt.Response = "550 token=legacy-token legacy@example.net";
                savedJob.LastError = "550 token=legacy-token legacy@example.net";
                await db.SaveChangesAsync();
            }
            var oldAttempts = await administration.GetDeliveryAttemptsAsync(leased[0].Id, CancellationToken.None);
            Assert.DoesNotContain("legacy-token", oldAttempts[0].Response);
            Assert.DoesNotContain("legacy@example.net", oldAttempts[0].Response);
            var oldJobs = await administration.SearchDeliveryJobsAsync(null, null, DeliveryJobScope.Active, 1, 20, CancellationToken.None);
            var oldJob = Assert.Single(oldJobs.Items, x => x.Id == leased[0].Id);
            Assert.DoesNotContain("legacy-token", oldJob.LastError);
            Assert.DoesNotContain("legacy@example.net", oldJob.LastError);
            var report = new TlsReportDelivery
            {
                PolicyDomain = "example.net",
                ReportUri = "mailto:tlsrpt@example.net",
                PeriodStart = DateTimeOffset.UtcNow.AddDays(-2),
                PeriodEnd = DateTimeOffset.UtcNow.AddDays(-1),
                ReportJson = "{}",
                Attempts = 5,
                LastError = "temporary failure",
                NextAttemptAt = DateTimeOffset.UtcNow.AddHours(-1)
            };
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.TlsReportDeliveries.Add(report);
                await db.SaveChangesAsync();
            }
            var retryKey = Guid.NewGuid().ToString();
            Assert.True((await administration.RetryTlsReportDeliveryAsync(report.Id, retryKey, "test", CancellationToken.None)).Scheduled);
            Assert.True((await administration.RetryTlsReportDeliveryAsync(report.Id, retryKey, "test", CancellationToken.None)).Replayed);
            Assert.False((await administration.RetryTlsReportDeliveryAsync(report.Id, Guid.NewGuid().ToString(), "test", CancellationToken.None)).Scheduled);
            await Assert.ThrowsAsync<FormatException>(() => administration.RetryTlsReportDeliveryAsync(report.Id, "invalid", "test", CancellationToken.None));
            await using (var db = await factory.CreateDbContextAsync())
            {
                var saved = await db.TlsReportDeliveries.SingleAsync(x => x.Id == report.Id);
                Assert.Equal(0, saved.Attempts);
                Assert.Null(saved.LastError);
                Assert.Equal(retryKey.Replace("-", ""), saved.LastManualRetryKey);
                Assert.Equal(1, await db.AuditRecords.CountAsync(x => x.Action == "tls-report.retry" && x.Resource == $"tls-report:{report.Id}"));
                saved.LeaseOwner = "test-worker";
                saved.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(5);
                await db.SaveChangesAsync();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => administration.RetryTlsReportDeliveryAsync(report.Id, Guid.NewGuid().ToString(), "test", CancellationToken.None));
            await using (var db = await factory.CreateDbContextAsync())
            {
                var saved = await db.TlsReportDeliveries.SingleAsync(x => x.Id == report.Id);
                saved.LeaseOwner = null;
                saved.LeaseUntil = null;
                saved.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                await db.SaveChangesAsync();
            }
            var inbound = new BlockingReportInbound();
            TlsReportBackgroundService Worker() => new(
                factory, null!, inbound,
                Options.Create(new TransportSecurityOptions { ReportContactInfo = "postmaster@example.net" }),
                NullLogger<TlsReportBackgroundService>.Instance);
            var firstWorker = Worker().DeliverReportsAsync(CancellationToken.None);
            await inbound.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Worker().DeliverReportsAsync(CancellationToken.None);
            Assert.Equal(1, inbound.AcceptCount);
            inbound.Release.SetResult();
            await firstWorker;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var delivered = await db.TlsReportDeliveries.SingleAsync(x => x.Id == report.Id);
                Assert.NotNull(delivered.CompletedAt);
                Assert.Null(delivered.LeaseOwner);
                Assert.Null(delivered.LeaseUntil);
            }
            await services.VerifyMailDatabaseAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class BlockingReportInbound : IInboundMessageService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AcceptCount { get; private set; }

        public Task<bool> IsLocalRecipientAsync(string address, CancellationToken cancellationToken) => Task.FromResult(false);

        public async Task<InboundAcceptance> AcceptAsync(InboundEnvelope envelope, Stream content, CancellationToken cancellationToken)
        {
            AcceptCount++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new InboundAcceptance(Guid.NewGuid(), new string('a', 64), 0, 1);
        }
    }

    [Fact]
    public void PemLoaderAcceptsCertificateChainFileAndPkcs8Key()
    {
        var root = CreateCertificateFiles(encrypted: false, mismatched: false, expired: false);
        try
        {
            using var loaded = PemCertificateLoader.Load(
                new PemCertificateOptions
                {
                    CertificatePath = Path.Combine(root, "chain.pem"),
                    PrivateKeyPath = Path.Combine(root, "key.pem")
                },
                "Test");

            Assert.True(loaded.Certificate.HasPrivateKey);
            Assert.Contains("mail.example.test", loaded.Certificate.Subject);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PemLoaderAcceptsEncryptedPrivateKey()
    {
        var root = CreateCertificateFiles(encrypted: true, mismatched: false, expired: false);
        try
        {
            using var loaded = PemCertificateLoader.Load(
                new PemCertificateOptions
                {
                    CertificatePath = Path.Combine(root, "chain.pem"),
                    PrivateKeyPath = Path.Combine(root, "key.pem"),
                    PrivateKeyPassword = "test-password"
                },
                "Test");

            Assert.True(loaded.Certificate.HasPrivateKey);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PemLoaderRejectsMismatchedPrivateKey()
    {
        var root = CreateCertificateFiles(encrypted: false, mismatched: true, expired: false);
        try
        {
            Assert.ThrowsAny<CryptographicException>(() =>
                PemCertificateLoader.Load(
                    new PemCertificateOptions
                    {
                        CertificatePath = Path.Combine(root, "chain.pem"),
                        PrivateKeyPath = Path.Combine(root, "key.pem")
                    },
                    "Test"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PemLoaderRejectsExpiredCertificate()
    {
        var root = CreateCertificateFiles(encrypted: false, mismatched: false, expired: true);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                PemCertificateLoader.Load(
                    new PemCertificateOptions
                    {
                        CertificatePath = Path.Combine(root, "chain.pem"),
                        PrivateKeyPath = Path.Combine(root, "key.pem")
                    },
                    "Test"));
            Assert.Contains("validity period", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DomainCertificateUploadPersistsMetadataAndSupportsSniSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-domain-cert-tests-{Guid.NewGuid():N}");
        var certificateRoot = CreateCertificateFiles(encrypted: true, mismatched: false, expired: false);
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")}",
                    ["Storage:RootPath"] = Path.Combine(root, "messages"),
                    ["SecurityAssets:RootPath"] = Path.Combine(root, "security-assets"),
                    ["Certificates:DefaultMailHostName"] = "mail.example.test"
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMailServerInfrastructure(configuration);
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<MailDbContext>>();
            Guid domainId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                var domain = new MailDomain { Name = "example.test", MailHostName = "mail.example.test" };
                db.Domains.Add(domain);
                await db.SaveChangesAsync();
                domainId = domain.Id;
            }

            var service = provider.GetRequiredService<DomainCertificateService>();
            var record = await service.SaveUploadAsync(
                domainId,
                await File.ReadAllBytesAsync(Path.Combine(certificateRoot, "chain.pem")),
                await File.ReadAllBytesAsync(Path.Combine(certificateRoot, "key.pem")),
                "test-password",
                "test",
                CancellationToken.None);

            Assert.Equal(CertificateSource.ManagedUpload, record.Source);
            Assert.True(File.Exists(record.CertificatePath));
            Assert.True(File.Exists(record.PrivateKeyPath));
            Assert.Contains("mail.example.test", record.SubjectAlternativeNames);

            var selector = provider.GetRequiredService<DomainCertificateProvider>();
            var options = await selector.SelectOptionsAsync(
                new SslClientHelloInfo("mail.example.test", SslProtocols.Tls13),
                CancellationToken.None);
            Assert.NotNull(options.ServerCertificateContext);
            var fallbackOptions = await selector.SelectOptionsAsync(
                new SslClientHelloInfo(string.Empty, SslProtocols.Tls13),
                CancellationToken.None);
            Assert.NotNull(fallbackOptions.ServerCertificateContext);
            var settings = provider.GetRequiredService<SystemSettingsService>();
            await settings.SaveCertificateSelectionSettingsAsync(
                new CertificateSelectionSettingsView(null, false, [], null),
                CancellationToken.None);
            selector.Invalidate();
            await Assert.ThrowsAsync<AuthenticationException>(async () =>
                await selector.SelectOptionsAsync(
                    new SslClientHelloInfo(string.Empty, SslProtocols.Tls13),
                    CancellationToken.None));
            var saved = await settings.SaveCertificateSelectionSettingsAsync(
                new CertificateSelectionSettingsView("mail.example.test", false, [], null),
                CancellationToken.None);
            selector.Invalidate();
            Assert.Equal("mail.example.test", saved.EffectiveDefaultMailHostName);
            var databaseDefaultOptions = await selector.SelectOptionsAsync(
                new SslClientHelloInfo(string.Empty, SslProtocols.Tls13),
                CancellationToken.None);
            Assert.NotNull(databaseDefaultOptions.ServerCertificateContext);
            await Assert.ThrowsAsync<AuthenticationException>(async () =>
                await selector.SelectOptionsAsync(
                    new SslClientHelloInfo("unknown.example.test", SslProtocols.Tls13),
                    CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
            Directory.Delete(certificateRoot, recursive: true);
        }
    }

    [Fact]
    public void DnsGeneratorBuildsSpfDmarcAndDkimRecords()
    {
        var domain = new MailDomain
        {
            Name = "example.test",
            MailHostName = "mail.example.test",
            DkimSelector = "s202606",
            DkimPublicKey = "AQID"
        };
        var settings = new DomainDnsSettings
        {
            SpfIncludeMx = true,
            SpfIncludes = "spf.relay.test",
            SpfAllPolicy = "-all",
            DmarcPolicy = "reject",
            DmarcAggregateReportUris = "mailto:dmarc@example.test"
        };

        var records = DomainDnsService.GenerateRecords(domain, settings);

        Assert.Contains(records, x => x.Name == "example.test" && x.Value == "v=spf1 mx include:spf.relay.test -all");
        Assert.Contains(records, x => x.Name == "_dmarc.example.test" && x.Value.Contains("p=reject", StringComparison.Ordinal));
        Assert.Contains(records, x => x.Name == "s202606._domainkey.example.test" && x.Value.Contains("p=AQID", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GeneratedDkimKeySignsOutboundMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-dkim-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")}",
                    ["Storage:RootPath"] = Path.Combine(root, "messages"),
                    ["SecurityAssets:RootPath"] = Path.Combine(root, "security-assets")
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMailServerInfrastructure(configuration);
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<MailDbContext>>();
            Guid domainId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                var domain = new MailDomain { Name = "example.test", MailHostName = "mail.example.test" };
                db.Domains.Add(domain);
                await db.SaveChangesAsync();
                domainId = domain.Id;
            }

            var dns = provider.GetRequiredService<DomainDnsService>();
            await dns.GenerateDkimAsync(domainId, "s202606", "test", CancellationToken.None);
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse("sender@example.test"));
            message.To.Add(MailboxAddress.Parse("recipient@example.net"));
            message.Subject = "DKIM test";
            message.Body = new TextPart("plain") { Text = "hello" };
            await using var input = new MemoryStream();
            await message.WriteToAsync(input);
            input.Position = 0;

            var signed = await OutboundMessage.LoadAndSignAsync(
                input,
                "sender@example.test",
                factory,
                CancellationToken.None);

            Assert.NotNull(signed.Headers[HeaderId.DkimSignature]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateCertificateFiles(bool encrypted, bool mismatched, bool expired)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-pem-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var issuerKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest(
            "CN=MailServer Test CA",
            issuerKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        issuerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            critical: true));
        issuerRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(issuerRequest.PublicKey, false));
        using var issuer = issuerRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow.AddDays(30));
        using var certificateKey = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=mail.example.test",
            certificateKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            critical: false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("mail.example.test");
        request.CertificateExtensions.Add(san.Build());
        var notBefore = expired ? DateTimeOffset.UtcNow.AddDays(-10) : DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(7);
        using var publicCertificate = request.Create(
            issuer,
            notBefore,
            notAfter,
            RandomNumberGenerator.GetBytes(16));
        using var certificate = publicCertificate.CopyWithPrivateKey(certificateKey);
        File.WriteAllText(
            Path.Combine(root, "chain.pem"),
            certificate.ExportCertificatePem() + Environment.NewLine + issuer.ExportCertificatePem());
        using var exportedKey = mismatched ? RSA.Create(2048) : null;
        var key = exportedKey ?? certificateKey;
        File.WriteAllText(
            Path.Combine(root, "key.pem"),
            encrypted
                ? key.ExportEncryptedPkcs8PrivateKeyPem(
                    "test-password",
                    new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000))
                : key.ExportPkcs8PrivateKeyPem());
        return root;
    }
}
