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
    public void DomainAuditResourcesUseBusinessSqlAndLogsUseIndependentSqliteQuery()
    {
        var options = new DbContextOptionsBuilder<MailDbContext>()
            .UseMySQL("Server=localhost;Database=mailserver_model_test;User ID=unused;Password=unused").Options;
        using var db = new MailDbContext(options);
        var resourcesSql = AdminApplicationService.GetDomainAuditResources(db, 123).ToQueryString();
        Assert.Contains("mail_domains", resourcesSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mail_accounts", resourcesSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mail_aliases", resourcesSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mail_rules", resourcesSql, StringComparison.OrdinalIgnoreCase);
        using var local = new LocalLogDbContext(new DbContextOptionsBuilder<LocalLogDbContext>().UseSqlite("Data Source=:memory:").Options);
        var resources = new[] { "domain:123", "account:456" };
        var sql = local.AuditRecords.Where(x => x.Resource == "settings:mailboxes" || resources.Contains(x.Resource)).ToQueryString();
        Assert.Contains("audit_records", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mail_domains", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(typeof(long), db.Model.FindEntityType(typeof(StoredMessage))!.FindProperty("Id")!.ClrType);
    }

    [Fact]
    public void DnsSettingsLongFieldsUseOffRowMySqlTextColumns()
    {
        var options = new DbContextOptionsBuilder<MailDbContext>()
            .UseMySQL("Server=localhost;Database=mailserver_model_test;User ID=unused;Password=unused")
            .Options;
        using var db = new MailDbContext(options, TestLocalLogs.Create());
        var entity = db.Model.FindEntityType(typeof(DomainDnsSettings));
        Assert.NotNull(entity);
        foreach (var propertyName in new[]
                 {
                     nameof(DomainDnsSettings.SpfIpv4),
                     nameof(DomainDnsSettings.SpfIpv6),
                     nameof(DomainDnsSettings.SpfIncludes),
                     nameof(DomainDnsSettings.SpfCustomMechanisms),
                     nameof(DomainDnsSettings.DmarcAggregateReportUris),
                     nameof(DomainDnsSettings.DmarcForensicReportUris),
                     nameof(DomainDnsSettings.MtaStsMxPatterns),
                     nameof(DomainDnsSettings.TlsReportUris),
                     nameof(DomainDnsSettings.DaneTlsaRecords)
                 })
            Assert.Equal("text", entity.FindProperty(propertyName)?.GetColumnType());
    }

    [Fact]
    public async Task MySqlMigrationVersionContentionAndTwoWorkerLeases()
    {
        var connection = Environment.GetEnvironmentVariable("MAILSERVER_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return; // Opt-in: the wrapper script requires this value.
        if (!connection.Contains("mailserver_acceptance", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MySQL acceptance tests require an isolated mailserver_acceptance database.");
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-mysql-acceptance-{MailServer.Domain.SnowflakeIds.NewId()}");
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
        var performance = provider.GetRequiredService<PerformanceCollector>();
        performance.Settings = new() { SampleRate = 1 };
        using (performance.Start("api", "mysql-acceptance"))
        {
            await using var db = await factory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Messages.CountAsync();
            await transaction.CommitAsync();
        }
        var performanceStorage = new PerformanceStorage(factory, performance, provider.GetRequiredService<ResourceRuntime>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PerformanceStorage>.Instance);
        await performanceStorage.FlushAsync(CancellationToken.None);
        Assert.False(performance.StorageUnavailable);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.True(await db.PerformanceOperations.AnyAsync(x => x.Name == "mysql-acceptance" && x.SqlCount > 0));
            Assert.True(await db.PerformanceMinutes.AnyAsync(x => x.ProcessId == performance.ProcessId));
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
            var message = new StoredMessage { ContentKey = MailServer.Domain.SnowflakeIds.NewId().ToString(), EnvelopeFrom = "sender@example.test" };
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
        var concurrent = (await first).Concat(await second).ToArray();
        Assert.Equal(concurrent.Length, concurrent.Select(x => x.Id).Distinct().Count());
        // SKIP LOCKED may return an underfilled batch while another transaction
        // holds scan/shared-message locks. The normal next poll must recover it.
        var remaining = concurrent.Length < 4
            ? await queue.LeaseAsync("acceptance-next-poll", 4 - concurrent.Length, TimeSpan.FromMinutes(1), CancellationToken.None)
            : [];
        var leased = concurrent.Concat(remaining).ToArray();
        Assert.Equal(4, leased.Length);
        Assert.Equal(4, leased.Select(x => x.Id).Distinct().Count());

        long mailboxId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = $"search-{MailServer.Domain.SnowflakeIds.NewId()}.example.test" };
            var account = new MailAccount { Domain = domain, LocalPart = "alice", PasswordHash = "unused" };
            var mailbox = new Mailbox { Account = account, Name = "INBOX" };
            db.AddRange(domain, account, mailbox);
            for (var uid = 1; uid <= 3; uid++)
                db.MailboxMessages.Add(new MailboxMessage
                {
                    Mailbox = mailbox,
                    Message = new StoredMessage { ContentKey = MailServer.Domain.SnowflakeIds.NewId().ToString(), EnvelopeFrom = "sender@example.test" },
                    Uid = uid,
                    Flags = uid switch { 1 => "\\SeenOther", 2 => "\\Seen", _ => "" }
                });
            await db.SaveChangesAsync();
            mailboxId = mailbox.Id;
        }
        await using (var db = await factory.CreateDbContextAsync())
        {
            var mailbox = await db.Mailboxes.Include(x => x.Account).SingleAsync(x => x.Id == mailboxId);
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            mailbox.Account.UsedBytes += 123;
            db.AuditRecords.Add(new AuditRecord { Actor = "acceptance", Action = "save-graph", Resource = "mailbox" });
            var message = new StoredMessage { ContentKey = MailServer.Domain.SnowflakeIds.NewId().ToString(), EnvelopeFrom = "sender@example.test" };
            db.MessageRecipients.Add(new StoredMessageRecipient { Message = message, Address = "alice@example.test" });
            await db.SaveChangesAsync();
            Assert.False(db.ChangeTracker.AutoDetectChangesEnabled);
            db.ChangeTracker.Clear();
            Assert.Equal(123, await db.Accounts.Where(x => x.Id == mailbox.AccountId).Select(x => x.UsedBytes).SingleAsync());
            Assert.True(await db.MessageRecipients.AnyAsync(x => x.MessageId == message.Id));
        }
        var mailboxService = new MailboxService(factory, null!, null!, null!);
        Assert.Equal([2], await mailboxService.SearchAsync(mailboxId, "SEEN", false, CancellationToken.None));
        Assert.Equal([1, 3], await mailboxService.SearchAsync(mailboxId, "UNSEEN", true, CancellationToken.None));
        long accountId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            accountId = await db.Mailboxes.Where(x => x.Id == mailboxId).Select(x => x.AccountId).SingleAsync();
            // Accent-insensitive database collations must not turn this into \\Seen.
            (await db.MailboxMessages.SingleAsync(x => x.MailboxId == mailboxId && x.Uid == 1)).Flags = "\\Séen";
            foreach (var messageId in await db.MailboxMessages.Where(x => x.MailboxId == mailboxId)
                         .Select(x => x.MessageId).ToListAsync())
                db.MessageSearchRecords.Add(new MessageSearchRecord
                {
                    MessageId = messageId, ParserVersion = MessageSearchBackfillService.ParserVersion,
                    Status = "Ready", Complete = true, Subject = "中文 RESUME", Body = "检索 NEEDLE"
                });
            await db.SaveChangesAsync();
        }
        var summary = await mailboxService.SelectAsync(accountId, "INBOX", CancellationToken.None);
        Assert.NotNull(summary);
        Assert.Equal(3, summary.Exists);
        Assert.Equal(2, summary.Unseen);
        // A null content store makes unexpected source fallback fail immediately.
        var indexedMailbox = new MailboxService(factory, null!, null!, null!,
            searchOptions: Microsoft.Extensions.Options.Options.Create(new MessageSearchOptions { Enabled = true }));
        Assert.Equal([1, 2, 3], await indexedMailbox.SearchAsync(mailboxId, "BODY 检索", true, CancellationToken.None));
        Assert.Equal([1, 2, 3], await indexedMailbox.SearchAsync(mailboxId, "SUBJECT resume", true, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var ids = await db.MailboxMessages.Where(x => x.MailboxId == mailboxId).Select(x => x.MessageId).ToArrayAsync();
            foreach (var record in await db.MessageSearchRecords.WhereIdIn(x => x.MessageId, ids).ToListAsync())
            {
                record.Status = "HeadersReady";
                record.Complete = false;
            }
            await db.SaveChangesAsync();
        }
        Assert.Empty(await indexedMailbox.SearchAsync(mailboxId, "SUBJECT absent", true, CancellationToken.None));
        Assert.Empty(await indexedMailbox.SearchAsync(mailboxId, "HEADER X-Missing absent", true, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var middle = await db.MailboxMessages.SingleAsync(x => x.MailboxId == mailboxId && x.Uid == 2);
            middle.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        await using (var snapshot = await mailboxService.CreateFetchSnapshotAsync(
            mailboxId, "3,1", true, CancellationToken.None))
        {
            var items = new List<MailboxItem>();
            await foreach (var item in snapshot.ReadAsync(CancellationToken.None)) items.Add(item);
            Assert.Equal([(1L, 1L), (3L, 2L)], items.Select(x => (x.Uid, x.Sequence)).ToArray());
        }
        var administration = provider.GetRequiredService<AdminApplicationService>();
        Assert.True((await administration.GetDeliveryQueueSummaryAsync(CancellationToken.None)).Processing >= 4);
        Assert.Equal([1, 3], await indexedMailbox.SearchAsync(mailboxId, "BODY needle", true, CancellationToken.None));
        Assert.Equal(7, (await administration.GetOverviewTrafficAsync(7, CancellationToken.None)).Buckets.Count);
        Assert.True((await administration.GetDashboardAsync(CancellationToken.None)).Domains >= 1);
    }
    [Fact]
    public async Task OrderedDomainWriterLocksKeepConcurrentQuotaUpdatesExact()
    {
        var connection = Environment.GetEnvironmentVariable("MAILSERVER_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        if (!connection.Contains("mailserver_acceptance", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An isolated acceptance database is required.");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Database:Provider"] = "MySql", ["ConnectionStrings:MailDatabase"] = connection }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IConfiguration>(configuration);
        services.AddMailServerInfrastructure(configuration, "worker");
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<MailDbContext>>();
        var domains = new[] { new MailDomain { Name = MailServer.Domain.SnowflakeIds.NewId().ToString() + ".test", QuotaBytes = 100000 },
            new MailDomain { Name = MailServer.Domain.SnowflakeIds.NewId().ToString() + ".test", QuotaBytes = 100000 } };
        var accounts = domains.Select(domain => new MailAccount { Domain = domain, LocalPart = "quota", PasswordHash = "unused", QuotaBytes = 100000 }).ToArray();
        await using (var db = await factory.CreateDbContextAsync())
        { await db.Database.MigrateAsync(); db.Accounts.AddRange(accounts); await db.SaveChangesAsync(); }
        await Task.WhenAll(Enumerable.Range(0, 16).Select(async index =>
        {
            await using var db = await factory.CreateDbContextAsync();
            var ids = index % 2 == 0 ? domains.Select(x => x.Id) : domains.Reverse().Select(x => x.Id);
            await using var transaction = await MailboxWriteCoordinator.BeginAsync(db, ids, CancellationToken.None);
            foreach (var accountId in accounts.Select(x => x.Id))
            {
                var account = await db.Accounts.Include(x => x.Domain).SingleAsync(x => x.Id == accountId);
                Assert.Equal(QuotaAvailability.Available, MailboxWriteAccounting.CheckQuota(account, 100));
                MailboxWriteAccounting.ApplyQuotaDelta(account, 100);
            }
            await db.SaveChangesAsync(); await transaction.CommitAsync();
        }));
        await using var verify = await factory.CreateDbContextAsync();
        foreach (var id in accounts.Select(x => x.Id))
            Assert.Equal(1600, await verify.Accounts.Where(x => x.Id == id).Select(x => x.UsedBytes).SingleAsync());
        foreach (var id in domains.Select(x => x.Id))
            Assert.Equal(1600, await verify.Domains.Where(x => x.Id == id).Select(x => x.UsedBytes).SingleAsync());
    }

    [Fact]
    public async Task SharedDomainAppendCopyMoveQuotaRollbackAndCancellationUseMySql()
    {
        var connection = Environment.GetEnvironmentVariable("MAILSERVER_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        if (!connection.Contains("mailserver_acceptance", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An isolated acceptance database is required.");
        var storage = Path.Combine(Path.GetTempPath(), "mailserver-mysql-writes-" + MailServer.Domain.SnowflakeIds.NewId().ToString());
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Database:Provider"] = "MySql", ["ConnectionStrings:MailDatabase"] = connection,
                ["Storage:RootPath"] = storage }).Build();
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IConfiguration>(configuration);
            services.AddMailServerInfrastructure(configuration, "worker");
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<MailDbContext>>();
            provider.GetRequiredService<ResourceRuntime>().LargeMime.Resize(2);
            var domain = new MailDomain { Name = MailServer.Domain.SnowflakeIds.NewId().ToString() + ".test", QuotaBytes = 1000000 };
            var accounts = new[] { "alice", "bob" }.Select(name => new MailAccount
                { Domain = domain, LocalPart = name, PasswordHash = "unused", QuotaBytes = 1000000 }).ToArray();
            var inboxes = accounts.Select(account => new Mailbox { Account = account, Name = "INBOX" }).ToArray();
            var target = new Mailbox { Account = accounts[0], Name = "Target" };
            await using (var db = await factory.CreateDbContextAsync())
            { await db.Database.MigrateAsync(); db.Mailboxes.AddRange(inboxes); db.Mailboxes.Add(target); await db.SaveChangesAsync(); }
            var sent = provider.GetRequiredService<SentCopyService>();
            var sizes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
            {
                var raw = System.Text.Encoding.UTF8.GetBytes($"Message-ID: <{MailServer.Domain.SnowflakeIds.NewId()}@test.invalid>\r\nSubject: 中文\r\n\r\nbody\r\n");
                await using var content = new MemoryStream(raw);
                await sent.AppendAsync(accounts[index % 2].Id, "INBOX", content, [], null, CancellationToken.None);
                return raw.Length;
            }));
            var mailboxes = provider.GetRequiredService<IMailboxService>();
            var copy = await mailboxes.CopyAsync(inboxes[0].Id, "1", true, "Target", false, CancellationToken.None);
            Assert.Single(copy.SourceUids);
            var move = await mailboxes.CopyAsync(inboxes[0].Id, "1:3", false, "Target", true, CancellationToken.None);
            Assert.Equal(3, move.SourceUids.Count);
            Assert.Equal(new long[] { 1, 2, 3 }, move.ExpungedItems.Select(x => x.Sequence));
            long quota;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var account = await db.Accounts.SingleAsync(x => x.Id == accounts[0].Id);
                quota = account.UsedBytes;
                Assert.Equal(sizes.Sum() + sizes[0], await db.Domains.Where(x => x.Id == domain.Id).Select(x => x.UsedBytes).SingleAsync());
                account.QuotaBytes = quota; await db.SaveChangesAsync();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => mailboxes.CopyAsync(inboxes[0].Id, "1:*", true, "Target", false, CancellationToken.None));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mailboxes.CopyAsync(inboxes[0].Id, "1:*", true, "Target", true, cancelled.Token));
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(quota, await verify.Accounts.Where(x => x.Id == accounts[0].Id).Select(x => x.UsedBytes).SingleAsync());
            Assert.Equal(1, await verify.MailboxMessages.CountAsync(x => x.MailboxId == inboxes[0].Id));
            Assert.Equal(4, await verify.MailboxMessages.CountAsync(x => x.MailboxId == target.Id));
            Assert.Equal(4, await verify.MailboxMessages.CountAsync(x => x.MailboxId == inboxes[1].Id));
        }
        finally { if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true); }
    }

    [Fact]
    public async Task UidStoreDeletedAndExpungePreserveMySqlQuotaAndCleanupOwnership()
    {
        var connection = Environment.GetEnvironmentVariable("MAILSERVER_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        if (!connection.Contains("mailserver_acceptance", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An isolated acceptance database is required.");
        var storage = Path.Combine(Path.GetTempPath(), "mailserver-mysql-expunge-" + MailServer.Domain.SnowflakeIds.NewId().ToString());
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Database:Provider"] = "MySql", ["ConnectionStrings:MailDatabase"] = connection,
                ["Storage:RootPath"] = storage }).Build();
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IConfiguration>(configuration);
            services.AddMailServerInfrastructure(configuration, "worker");
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<MailDbContext>>();
            var domain = new MailDomain { Name = MailServer.Domain.SnowflakeIds.NewId().ToString() + ".test", QuotaBytes = 1000000 };
            var account = new MailAccount { Domain = domain, LocalPart = "inbox", PasswordHash = "unused", QuotaBytes = 1000000 };
            var inbox = new Mailbox { Account = account, Name = "INBOX" };
            await using (var db = await factory.CreateDbContextAsync())
            { await db.Database.MigrateAsync(); db.Mailboxes.Add(inbox); await db.SaveChangesAsync(); }
            var sent = provider.GetRequiredService<SentCopyService>();
            var sizes = new List<int>();
            for (var i = 0; i < 2; i++)
            {
                var raw = System.Text.Encoding.UTF8.GetBytes($"Message-ID: <{MailServer.Domain.SnowflakeIds.NewId()}@test.invalid>\r\nSubject: 中文 {i}\r\n\r\nbody {i}\r\n");
                sizes.Add(raw.Length);
                await using var content = new MemoryStream(raw);
                await sent.AppendAsync(account.Id, "INBOX", content, [], null, CancellationToken.None);
            }
            long deletedId;
            long beforeModSeq;
            var existingCleanup = new CleanupQueueItem { ResourceType = CleanupResourceType.MailboxMessage,
                Status = CleanupQueueStatus.Failed, AccountId = account.Id, PurgeAfter = DateTimeOffset.UtcNow.AddDays(1) };
            var unrelatedCleanup = new CleanupQueueItem { ResourceType = CleanupResourceType.MailboxMessage,
                Status = CleanupQueueStatus.Pending, AccountId = account.Id, ResourceId = null,
                PurgeAfter = DateTimeOffset.UtcNow.AddDays(2) };
            await using (var db = await factory.CreateDbContextAsync())
            {
                var first = await db.MailboxMessages.Include(x => x.Message).SingleAsync(x => x.MailboxId == inbox.Id && x.Uid == 1);
                deletedId = first.Id; beforeModSeq = first.ModSeq;
                existingCleanup.ResourceId = deletedId; existingCleanup.ContentKey = first.Message.ContentKey;
                db.CleanupQueue.AddRange(existingCleanup, unrelatedCleanup); await db.SaveChangesAsync();
            }
            var mailboxes = provider.GetRequiredService<IMailboxService>();
            await mailboxes.StoreFlagsAsync(inbox.Id, "1", useUid: true, "+FLAGS", ["\\Deleted"], CancellationToken.None);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var first = await db.MailboxMessages.SingleAsync(x => x.Id == deletedId);
                Assert.Contains("\\Deleted", first.Flags); Assert.True(first.ModSeq > beforeModSeq);
                Assert.Equal(1, first.Uid); Assert.False(first.QuotaReleased);
                Assert.Equal(sizes.Sum(), await db.Accounts.Where(x => x.Id == account.Id).Select(x => x.UsedBytes).SingleAsync());
            }
            var expunged = await mailboxes.ExpungeAsync(inbox.Id, CancellationToken.None);
            var item = Assert.Single(expunged); Assert.Equal(1, item.Uid); Assert.Equal(1, item.Sequence);
            Assert.Empty(await mailboxes.ExpungeAsync(inbox.Id, CancellationToken.None));
            await using var verify = await factory.CreateDbContextAsync();
            var deleted = await verify.MailboxMessages.IgnoreQueryFilters().SingleAsync(x => x.Id == deletedId);
            Assert.NotNull(deleted.DeletedAt); Assert.True(deleted.QuotaReleased);
            Assert.Equal(sizes[1], await verify.Accounts.Where(x => x.Id == account.Id).Select(x => x.UsedBytes).SingleAsync());
            Assert.Equal(sizes[1], await verify.Domains.Where(x => x.Id == domain.Id).Select(x => x.UsedBytes).SingleAsync());
            Assert.Equal(2, (await verify.MailboxMessages.SingleAsync(x => x.MailboxId == inbox.Id)).Uid);
            Assert.Single(await verify.QuotaLedger.Where(x => x.MailboxMessageId == deletedId && x.Operation == QuotaLedgerOperation.Remove).ToListAsync());
            Assert.Single(await verify.CleanupQueue.Where(x => x.ResourceType == CleanupResourceType.MailboxMessage && x.ResourceId == deletedId).ToListAsync());
            Assert.Null((await verify.CleanupQueue.SingleAsync(x => x.Id == unrelatedCleanup.Id)).ResourceId);
            Assert.Equal(existingCleanup.Id, (await verify.CleanupQueue.SingleAsync(x => x.ResourceId == deletedId)).Id);
        }
        finally { if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true); }
    }

    [Theory]
    [InlineData("Sqlite")]
    [InlineData("MySql")]
    public async Task LargeCopyKeepsTrackingBoundedAndMidBatchFailureRollsBack(string provider)
    {
        var connection = Environment.GetEnvironmentVariable("MAILSERVER_TEST_MYSQL_CONNECTION");
        if (provider == "MySql" && string.IsNullOrWhiteSpace(connection)) return;
        if (provider == "MySql" && !connection!.Contains("mailserver_acceptance", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An isolated acceptance database is required.");
        var path = Path.Combine(Path.GetTempPath(), "mailserver-bounded-copy-" + MailServer.Domain.SnowflakeIds.NewId().ToString() + ".db");
        var tracker = new CopyTrackingInterceptor();
        var options = new DbContextOptionsBuilder<MailDbContext>().AddInterceptors(tracker);
        if (provider == "MySql") options.UseMySQL(connection!, mysql => mysql.MigrationsAssembly("MailServer.Migrations.MySql"));
        else { SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3()); options.UseSqlite("Data Source=" + path + ";Pooling=False"); }
        var factory = new TestMailDbContextFactory(options.Options);
        try
        {
            var domain = new MailDomain { Name = MailServer.Domain.SnowflakeIds.NewId().ToString() + ".test", QuotaBytes = 1000000, UsedBytes = 2048 };
            var account = new MailAccount { Domain = domain, LocalPart = "inbox", PasswordHash = "unused", QuotaBytes = 1000000, UsedBytes = 2048 };
            var source = new Mailbox { Account = account, Name = "INBOX", UidNext = 2049, HighestModSeq = 2049 };
            var target = new Mailbox { Account = account, Name = "Target" };
            var rollback = new Mailbox { Account = account, Name = "Rollback" };
            var message = new StoredMessage { ContentKey = MailServer.Domain.SnowflakeIds.NewId().ToString(), EnvelopeFrom = "sender@test.invalid", Size = 1 };
            await using (var db = await factory.CreateDbContextAsync())
            {
                if (provider == "MySql") await db.Database.MigrateAsync(); else await db.Database.EnsureCreatedAsync();
                db.Mailboxes.AddRange(source, target, rollback);
                for (var uid = 1; uid <= 2048; uid++) db.MailboxMessages.Add(new MailboxMessage
                    { Mailbox = source, Message = message, Uid = uid, ModSeq = uid });
                await db.SaveChangesAsync();
            }
            var service = new MailboxService(factory, null!, null!, null!);
            tracker.Reset();
            var copied = await service.CopyAsync(source.Id, "1:*", true, "Target", false, CancellationToken.None);
            Assert.Equal(2048, copied.SourceUids.Count); Assert.Equal(2048, copied.TargetUids.Count);
            // At most 256 source references, 256 target references, 256 ledger rows,
            // 256 distinct stored messages and the constant mailbox/account/domain graph.
            Assert.InRange(tracker.PeakEntries, 1, 1030);
            Assert.True(tracker.Saves >= 9);
            tracker.Reset(failAfterSave: 2);
            await Assert.ThrowsAsync<CopyBatchFault>(() => service.CopyAsync(source.Id, "1:*", true, "Rollback", false, CancellationToken.None));
            tracker.Reset();
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(2048, await verify.MailboxMessages.CountAsync(x => x.MailboxId == source.Id));
            Assert.Equal(2048, await verify.MailboxMessages.CountAsync(x => x.MailboxId == target.Id));
            Assert.Equal(0, await verify.MailboxMessages.CountAsync(x => x.MailboxId == rollback.Id));
            var rolledBack = await verify.Mailboxes.SingleAsync(x => x.Id == rollback.Id);
            Assert.Equal(1, rolledBack.UidNext); Assert.Equal(1, rolledBack.HighestModSeq);
            Assert.Equal(4096, await verify.Accounts.Where(x => x.Id == account.Id).Select(x => x.UsedBytes).SingleAsync());
            Assert.Equal(4096, await verify.Domains.Where(x => x.Id == domain.Id).Select(x => x.UsedBytes).SingleAsync());
            Assert.Equal(2048, await verify.QuotaLedger.CountAsync(x => x.AccountId == account.Id));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class CopyBatchFault : Exception { }
    private sealed class CopyTrackingInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        internal int PeakEntries;
        internal int Saves;
        private int _failAfterSave;
        internal void Reset(int failAfterSave = 0) { PeakEntries = 0; Saves = 0; _failAfterSave = failAfterSave; }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData data,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            PeakEntries = Math.Max(PeakEntries, data.Context!.ChangeTracker.Entries().Count());
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData data,
            int result, CancellationToken cancellationToken = default)
        {
            if (++Saves == _failAfterSave) throw new CopyBatchFault();
            return ValueTask.FromResult(result);
        }
    }

}
