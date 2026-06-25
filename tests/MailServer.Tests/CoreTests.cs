using System.Text;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailServer.Tests;

public sealed class CoreTests
{
    [Fact]
    public void PasswordHashesAreVersionedAndVerifiable()
    {
        var service = new CredentialService();
        var hash = service.HashPassword("a strong test password");

        Assert.True(service.VerifyPassword(hash, "a strong test password", out _));
        Assert.False(service.VerifyPassword(hash, "wrong password", out _));
    }

    [Theory]
    [InlineData("1", 1, 10, true)]
    [InlineData("2:4", 3, 10, true)]
    [InlineData("4:2", 3, 10, true)]
    [InlineData("1,3,5", 4, 10, false)]
    [InlineData("*", 10, 10, true)]
    [InlineData("8:*", 9, 10, true)]
    public void SequenceSetsFollowImapRanges(string expression, long value, long maximum, bool expected)
    {
        Assert.Equal(expected, SequenceSet.Contains(expression, value, maximum));
    }

    [Fact]
    public async Task ContentStoreUsesSha256AndDeduplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 1024 }),
                factory);
            await using var first = new MemoryStream(Encoding.UTF8.GetBytes("Subject: test\r\n\r\nhello"));
            await using var second = new MemoryStream(Encoding.UTF8.GetBytes("Subject: test\r\n\r\nhello"));

            var firstResult = await store.SaveAsync(first, CancellationToken.None);
            var secondResult = await store.SaveAsync(second, CancellationToken.None);

            Assert.Equal(firstResult.Key, secondResult.Key);
            Assert.Equal(64, firstResult.Key.Length);
            await using var stored = await store.OpenReadAsync(firstResult.Key, CancellationToken.None);
            using var reader = new StreamReader(stored);
            Assert.Contains("hello", await reader.ReadToEndAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnauthenticatedInboundDeliveryCannotRelay()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var service = new InboundMessageService(factory, store);
            await using var message = new MemoryStream(Encoding.UTF8.GetBytes(
                "From: sender@example.net\r\nTo: victim@example.org\r\nSubject: relay\r\n\r\nblocked"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.AcceptAsync(
                    new InboundEnvelope(
                        "sender@example.net",
                        ["victim@example.org"],
                        null,
                        "127.0.0.1",
                        "client.example.net"),
                    message,
                    CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalInboundDeliveryCreatesInboxMessageWithUid()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com" };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused",
                    QuotaBytes = 4096
                };
                db.AddRange(domain, account, new Mailbox { Account = account, Name = "INBOX" });
                await db.SaveChangesAsync();
            }

            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var service = new InboundMessageService(factory, store);
            await using var message = new MemoryStream(Encoding.UTF8.GetBytes(
                "From: sender@example.net\r\nTo: alice@example.com\r\nSubject: local\r\n\r\nhello"));

            var result = await service.AcceptAsync(
                new InboundEnvelope(
                    "sender@example.net",
                    ["alice@example.com"],
                    null,
                    "127.0.0.1",
                    "client.example.net"),
                message,
                CancellationToken.None);

            Assert.Equal(1, result.LocalDeliveries);
            Assert.Equal(0, result.QueuedDeliveries);
            await using var verification = await factory.CreateDbContextAsync();
            var delivered = await verification.MailboxMessages.Include(x => x.Message).SingleAsync();
            Assert.Equal(1, delivered.Uid);
            Assert.Equal("sender@example.net", delivered.Message.EnvelopeFrom);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ApiSecretsUseConstantTimeComparableHashes()
    {
        var service = new CredentialService();
        var hash = service.HashApiSecret("secret-value");

        Assert.True(service.VerifyApiSecret(hash, "secret-value"));
        Assert.False(service.VerifyApiSecret(hash, "different-value"));
    }

    private static IDbContextFactory<MailDbContext> CreateFactory()
    {
        var options = new DbContextOptionsBuilder<MailDbContext>()
            .UseInMemoryDatabase($"mailserver-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new PooledDbContextFactory<MailDbContext>(options);
    }
}
