using System.Net;
using MailServer.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MailServer.Tests;

public sealed class ConnectionProtectionTests
{
    private static ConnectionProtection Create() => new(new ConfigurationBuilder().Build());
    private static readonly IPAddress Peer = IPAddress.Parse("192.0.2.10");

    [Fact]
    public void AuthenticationFailuresAreIsolatedAndExpireWithoutExtendingTheBan()
    {
        var protection = Create();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        for (var i = 0; i < 4; i++)
            Assert.False(protection.RecordAuthFailure(ProtectedLoginProtocol.Smtp, Peer, now.AddSeconds(i)));
        Assert.True(protection.RecordAuthFailure(ProtectedLoginProtocol.Smtp, Peer, now.AddSeconds(4)));
        Assert.True(protection.IsAuthBlocked(ProtectedLoginProtocol.Smtp, Peer, now.AddMinutes(1)));
        Assert.False(protection.IsAuthBlocked(ProtectedLoginProtocol.Imap, Peer, now.AddMinutes(1)));
        Assert.True(protection.RecordAuthFailure(ProtectedLoginProtocol.Smtp, Peer, now.AddMinutes(2)));
        Assert.False(protection.IsAuthBlocked(ProtectedLoginProtocol.Smtp, Peer, now.AddMinutes(31)));
        Assert.Equal(ConnectionProtection.Normalize(Peer),
            ConnectionProtection.Normalize(IPAddress.Parse("::ffff:192.0.2.10")));
    }

    [Fact]
    public void ConnectionQuotaCountsActiveSessionsAndRecoversWhenReleased()
    {
        var protection = Create();
        protection.Apply(ConnectionProtectionSettings.Default with
        {
            SmtpSubmission = new ConnectionQuota(60, 2, 1)
        });
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        Assert.True(protection.TryAcquire(ProtectedConnectionGroup.SmtpSubmission, Peer, now, out var first));
        Assert.False(protection.TryAcquire(ProtectedConnectionGroup.SmtpSubmission, Peer, now, out _));
        first!.Dispose();
        first.Dispose();
        Assert.True(protection.TryAcquire(ProtectedConnectionGroup.SmtpSubmission, Peer, now, out var second));
        second!.Dispose();
        Assert.False(protection.TryAcquire(ProtectedConnectionGroup.SmtpSubmission, Peer, now, out _));
        Assert.True(protection.TryAcquire(ProtectedConnectionGroup.SmtpSubmission, Peer, now.AddMinutes(1), out var renewed));
        renewed!.Dispose();
    }

    [Fact]
    public void CapacityRemainsBoundedWhileProtectedSourcesAreRetained()
    {
        var protection = Create();
        protection.Apply(ConnectionProtectionSettings.Default with { MaximumTrackedSources = 128 });
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        for (var i = 0; i < 128; i++)
        {
            var address = IPAddress.Parse($"198.51.100.{i}");
            Assert.False(protection.RecordAuthFailure(ProtectedLoginProtocol.Imap, address, now));
        }
        Assert.Equal(128, protection.TrackedSources);
        Assert.True(protection.RecordAuthFailure(ProtectedLoginProtocol.Imap, Peer, now));
        Assert.Equal(128, protection.TrackedSources);
        Assert.False(protection.TryAcquire(ProtectedConnectionGroup.Imap, Peer, now, out _));
    }

    [Fact]
    public void LoweringCapacityPreservesActivePermitsUntilTheyComplete()
    {
        var protection = Create();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var permits = new List<IDisposable>();
        for (var i = 0; i < 140; i++)
        {
            Assert.True(protection.TryAcquire(ProtectedConnectionGroup.Imap,
                IPAddress.Parse($"198.51.100.{i}"), now, out var permit));
            permits.Add(permit!);
        }
        protection.Apply(ConnectionProtectionSettings.Default with { MaximumTrackedSources = 128 });
        Assert.Equal(140, protection.TrackedSources);
        Assert.False(protection.TryAcquire(ProtectedConnectionGroup.Imap, Peer, now, out _));
        foreach (var permit in permits) permit.Dispose();
        Assert.True(protection.TryAcquire(ProtectedConnectionGroup.Imap, Peer, now.AddMinutes(3), out var next));
        next!.Dispose();
        Assert.True(protection.TrackedSources <= 128);
    }

    [Theory]
    [InlineData(0, 4096)]
    [InlineData(60, 0)]
    [InlineData(60, 65537)]
    public void RejectsInvalidSettings(int rate, int capacity)
    {
        var settings = ConnectionProtectionSettings.Default with
        {
            SmtpInbound = new ConnectionQuota(rate, 30, 16), MaximumTrackedSources = capacity
        };
        Assert.Throws<ArgumentException>(settings.Validate);
    }
}
