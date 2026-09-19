using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailServer.Tests;

public sealed class ClusterInvitationSqliteTests
{
    [Fact]
    public async Task ListingNeverReturnsTokenAndDatabaseStoresOnlyItsHash()
    {
        await using var fixture = await Fixture.CreateAsync();
        var invitation = await fixture.Service.CreateInvitationAsync(ClusterNodeRole.Witness, "test-admin", CancellationToken.None);
        Assert.NotNull(invitation.Token);
        Assert.Equal(fixture.Clock.UtcNow.AddMinutes(15), invitation.ExpiresAt);
        var listed = Assert.Single(await fixture.Service.ListInvitationsAsync(CancellationToken.None));
        Assert.Equal(invitation.Id, listed.Id);
        Assert.Null(listed.Token);
        Assert.DoesNotContain(invitation.Token!, JsonSerializer.Serialize(listed));
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var stored = await db.ClusterJoinInvitations.SingleAsync();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invitation.Token!))).ToLowerInvariant(), stored.TokenHash);
        Assert.DoesNotContain(invitation.Token!, JsonSerializer.Serialize(await db.AuditRecords.ToListAsync()));
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("used")]
    [InlineData("unknown")]
    public async Task UnusableInvitationCannotCreateNodeOrCertificate(string reason)
    {
        await using var fixture = await Fixture.CreateAsync();
        var invitation = await fixture.Service.CreateInvitationAsync(ClusterNodeRole.Standby, "test-admin", CancellationToken.None);
        if (reason == "revoked")
            await fixture.Service.RevokeInvitationAsync(invitation.Id, "test-admin", CancellationToken.None);
        if (reason == "expired") fixture.Clock.UtcNow = invitation.ExpiresAt;
        if (reason == "used")
        {
            await using var db = await fixture.Factory.CreateDbContextAsync();
            (await db.ClusterJoinInvitations.SingleAsync()).UsedAt = fixture.Clock.UtcNow;
            await db.SaveChangesAsync();
        }
        var request = new ClusterJoinWireRequest(reason == "unknown" ? "unknown-token" : invitation.Token!,
            "candidate", "Candidate", "tls://candidate.example.test:7443", "nonempty-csr-not-reached",
            await fixture.Fingerprint.GetAsync(CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.JoinWithCertificateAsync(request, CancellationToken.None));
        await using var after = await fixture.Factory.CreateDbContextAsync();
        Assert.False(await after.ClusterNodes.AnyAsync(x => x.Id == "candidate"));
        Assert.False(await after.ClusterNodeCertificates.AnyAsync(x => x.NodeId == "candidate"));
        Assert.False(await after.ClusterSnapshots.AnyAsync(x => x.NodeId == "candidate"));
        if (reason == "revoked")
        {
            Assert.True(Assert.Single(await fixture.Service.ListInvitationsAsync(CancellationToken.None)).Revoked);
            Assert.True(await after.AuditRecords.AnyAsync(x => x.Action == "cluster.invitation.revoke"));
        }
    }

    [Fact]
    public async Task RevokingMissingInvitationIsNotFound()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.RevokeInvitationAsync(Guid.NewGuid(), "test-admin", CancellationToken.None));
    }

    private sealed class Fixture(SqliteConnection connection, PooledDbContextFactory<MailDbContext> factory) : IAsyncDisposable
    {
        public PooledDbContextFactory<MailDbContext> Factory { get; } = factory;
        public TestClock Clock { get; } = new();
        public ClusterManagementService Service { get; private set; } = null!;
        public ReplicationSchemaFingerprintProvider Fingerprint { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            SqliteNative.Initialize();
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new PooledDbContextFactory<MailDbContext>(new DbContextOptionsBuilder<MailDbContext>().UseSqlite(connection).Options);
            var fixture = new Fixture(connection, factory);
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();
                db.ClusterConfigurations.Add(new ClusterConfiguration { Name = "test-cluster", PrimaryNodeId = "primary", CreatedBy = "test-admin" });
                db.ClusterNodes.Add(new ClusterNode { Id = "primary", Name = "Primary", Role = ClusterNodeRole.Primary, Endpoint = "tls://primary.example.test:7443" });
                await db.SaveChangesAsync();
                fixture.Fingerprint = new ReplicationSchemaFingerprintProvider(factory);
                fixture.Service = new ClusterManagementService(factory,
                    Options.Create(new ClusterOptions { NodeId = "primary", NodeName = "Primary" }), fixture.Clock,
                    new ClusterRuntimeState(true), new ClusterTransportRuntimeState(), new TransportOptions(),
                    new ClusterCertificateService(factory, new EphemeralDataProtectionProvider(), fixture.Clock),
                    fixture.Fingerprint, new ClusterPluginPackageService(Options.Create(new PluginOptions())), new TestHealth());
                return fixture;
            }
            catch { await connection.DisposeAsync(); throw; }
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
    }
    private sealed class TransportOptions : IOptionsMonitor<ClusterTransportOptions>
    {
        public ClusterTransportOptions CurrentValue { get; } = new();
        public ClusterTransportOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ClusterTransportOptions, string?> listener) => null;
    }
    private sealed class TestHealth : IClusterHealthService
    {
        public Task<ClusterHealth> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ClusterHealth(true, true, "primary", ClusterNodeRole.Primary, true, 0, 0));
    }
}
