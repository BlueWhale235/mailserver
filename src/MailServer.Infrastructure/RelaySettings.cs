using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MailServer.Infrastructure;

public sealed record RelaySettings(
    string Host = "",
    int Port = 18443,
    string PairingHost = "",
    int PairingPort = 18444,
    bool ReceiveEnabled = false,
    bool SendEnabled = false,
    string? PeerFingerprint = null,
    string? PendingId = null,
    string? PendingPasswordHash = null,
    DateTimeOffset? PendingExpiresAt = null,
    int MaxInboundConnections = 32,
    int MaxConnectionsPerIp = 4,
    int IdleTimeoutSeconds = 120);

public sealed record RelaySettingsView(
    string Host, int Port, string PairingHost, int PairingPort,
    bool ReceiveEnabled, bool SendEnabled, bool Paired, bool PairingPending,
    string? PeerFingerprint, DateTimeOffset? PendingExpiresAt,
    int MaxInboundConnections, int MaxConnectionsPerIp, int IdleTimeoutSeconds);

public sealed record RelaySettingsUpdate(
    string Host, int Port, string PairingHost, int PairingPort,
    bool ReceiveEnabled, bool SendEnabled,
    int MaxInboundConnections = 32, int MaxConnectionsPerIp = 4, int IdleTimeoutSeconds = 120);

public sealed record RelayPairingInvitation(string Command, string Password, DateTimeOffset ExpiresAt);
public sealed record RelayProcessStatus(string Role, bool Connected, int ActiveStreams,
    DateTimeOffset? LastConnected, string? LastError, DateTimeOffset ReportedAt,
    RelayInboundLimits? AppliedInboundLimits = null);
public sealed record RelayStatusView(bool Connected, int ActiveStreams,
    DateTimeOffset? LastConnected, string? LastError, IReadOnlyList<RelayProcessStatus> Processes,
    RelayInboundLimits? AppliedInboundLimits);

/// <summary>One global relay binding. The one-time secret is stored only as a digest.</summary>
public sealed class RelaySettingsService(IDbContextFactory<MailDbContext> dbFactory, RelayIdentity identity)
{
    private const string Key = "relay:configuration";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static long _nextStatusCleanupTicks;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RelaySettings> ReadAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var value = await db.SystemSettings.AsNoTracking().Where(x => x.Key == Key)
            .Select(x => x.Value).SingleOrDefaultAsync(token);
        return value is null ? new RelaySettings() : JsonSerializer.Deserialize<RelaySettings>(value, Json) ?? new RelaySettings();
    }

    public async Task<RelaySettingsView> ViewAsync(CancellationToken token) => View(await ReadAsync(token));

    public async Task ReportStatusAsync(RelayProcessStatus status, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var key = "relay:status:" + Environment.MachineName[..Math.Min(Environment.MachineName.Length, 32)]
            + ":" + Environment.ProcessId + ":" + status.Role;
        var row = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == key, token);
        if (row is null) db.SystemSettings.Add(new SystemSetting { Key = key, Value = JsonSerializer.Serialize(status, Json) });
        else row.Value = JsonSerializer.Serialize(status, Json);
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var due = Interlocked.Read(ref _nextStatusCleanupTicks);
        if (nowTicks >= due && Interlocked.CompareExchange(ref _nextStatusCleanupTicks,
                nowTicks + TimeSpan.FromHours(1).Ticks, due) == due)
        {
            var previous = await db.SystemSettings.Where(x => x.Key.StartsWith("relay:status:") && x.Key != key)
                .ToArrayAsync(token);
            foreach (var stale in previous)
            {
                var report = JsonSerializer.Deserialize<RelayProcessStatus>(stale.Value, Json);
                if (report is null || report.ReportedAt < DateTimeOffset.UtcNow.AddDays(-1))
                    db.SystemSettings.Remove(stale);
            }
        }
        await db.SaveChangesAsync(token);
    }

    public async Task<RelayStatusView> ReadStatusAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var rows = await db.SystemSettings.AsNoTracking().Where(x => x.Key.StartsWith("relay:status:"))
            .Select(x => x.Value).ToArrayAsync(token);
        var processes = rows.Select(x => JsonSerializer.Deserialize<RelayProcessStatus>(x, Json))
            .Where(x => x is not null && x.ReportedAt > DateTimeOffset.UtcNow.AddSeconds(-15))
            .Cast<RelayProcessStatus>().ToArray();
        return new RelayStatusView(processes.Any(x => x.Connected), processes.Sum(x => x.ActiveStreams),
            processes.MaxBy(x => x.LastConnected)?.LastConnected,
            processes.Where(x => x.LastError is not null).MaxBy(x => x.ReportedAt)?.LastError,
            processes, processes.Where(x => x.Connected && x.AppliedInboundLimits is not null)
                .MaxBy(x => x.ReportedAt)?.AppliedInboundLimits);
    }

    public async Task<RelaySettingsView> SaveAsync(RelaySettingsUpdate update, CancellationToken token)
    {
        Validate(update);
        return View(await ChangeAsync(current =>
        {
            if (((update.ReceiveEnabled && !current.ReceiveEnabled) ||
                 (update.SendEnabled && !current.SendEnabled)) && current.PeerFingerprint is null)
                throw new InvalidOperationException("Pair a relay before enabling mail traffic.");
            return current with
            {
                Host = update.Host.Trim(), Port = update.Port,
                PairingHost = update.PairingHost.Trim(), PairingPort = update.PairingPort,
                ReceiveEnabled = update.ReceiveEnabled, SendEnabled = update.SendEnabled,
                MaxInboundConnections = update.MaxInboundConnections,
                MaxConnectionsPerIp = update.MaxConnectionsPerIp,
                IdleTimeoutSeconds = update.IdleTimeoutSeconds
            };
        }, token));
    }

    public async Task<RelayPairingInvitation> CreateInvitationAsync(CancellationToken token)
    {
        var settings = await ReadAsync(token);
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.PairingHost))
            throw new InvalidOperationException("Save relay and public pairing host names first.");
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);
        var updated = await ChangeAsync(current => current with
        {
            PeerFingerprint = null,
            PendingId = id, PendingPasswordHash = Hash(password), PendingExpiresAt = expires
        }, token);
        return new RelayPairingInvitation(
            $"mailserver relay pair --host {updated.PairingHost} --port {updated.PairingPort} --id {id} --fingerprint {identity.Fingerprint}",
            password, expires);
    }

    public Task<RelaySettingsView> CancelInvitationAsync(CancellationToken token) =>
        ChangeViewAsync(current => current with { PendingId = null, PendingPasswordHash = null, PendingExpiresAt = null }, token);

    public Task<RelaySettingsView> UnpairAsync(CancellationToken token) =>
        ChangeViewAsync(current => current with
        {
            PeerFingerprint = null, PendingId = null, PendingPasswordHash = null, PendingExpiresAt = null,
            ReceiveEnabled = current.ReceiveEnabled, SendEnabled = current.SendEnabled
        }, token);

    private async Task<RelaySettingsView> ChangeViewAsync(Func<RelaySettings, RelaySettings> change, CancellationToken token) =>
        View(await ChangeAsync(change, token));

    public async Task<bool> CompletePairingAsync(string id, string password, string peerFingerprint, CancellationToken token)
    {
        var accepted = false;
        await ChangeAsync(current =>
        {
            if (current.PeerFingerprint is not null || current.PendingId != id ||
                current.PendingExpiresAt <= DateTimeOffset.UtcNow || current.PendingPasswordHash is null ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(current.PendingPasswordHash), Convert.FromHexString(Hash(password))))
                return current;
            accepted = true;
            return current with
            {
                PeerFingerprint = peerFingerprint, PendingId = null,
                PendingPasswordHash = null, PendingExpiresAt = null
            };
        }, token);
        return accepted;
    }

    private async Task<RelaySettings> ChangeAsync(Func<RelaySettings, RelaySettings> change, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            var row = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == Key, token);
            var current = row is null ? new RelaySettings() : JsonSerializer.Deserialize<RelaySettings>(row.Value, Json) ?? new RelaySettings();
            var updated = change(current);
            if (row is null) db.SystemSettings.Add(new SystemSetting { Key = Key, Value = JsonSerializer.Serialize(updated, Json) });
            else row.Value = JsonSerializer.Serialize(updated, Json);
            await db.SaveChangesAsync(token);
            return updated;
        }
        finally { Gate.Release(); }
    }

    private static RelaySettingsView View(RelaySettings value) => new(
        value.Host, value.Port, value.PairingHost, value.PairingPort,
        value.ReceiveEnabled, value.SendEnabled, value.PeerFingerprint is not null,
        value.PendingId is not null && value.PendingExpiresAt > DateTimeOffset.UtcNow,
        value.PeerFingerprint, value.PendingExpiresAt,
        value.MaxInboundConnections, value.MaxConnectionsPerIp, value.IdleTimeoutSeconds);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void Validate(RelaySettingsUpdate value)
    {
        if (!Uri.CheckHostName(value.Host.Trim()).Equals(UriHostNameType.Dns) &&
            !System.Net.IPAddress.TryParse(value.Host.Trim(), out _))
            throw new ArgumentException("Invalid relay host.");
        if (!Uri.CheckHostName(value.PairingHost.Trim()).Equals(UriHostNameType.Dns) &&
            !System.Net.IPAddress.TryParse(value.PairingHost.Trim(), out _))
            throw new ArgumentException("Invalid pairing host.");
        if (value.Port is < 1 or > 65535 || value.PairingPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(value), "Invalid relay port.");
        new RelayInboundLimits(value.MaxInboundConnections, value.MaxConnectionsPerIp,
            value.IdleTimeoutSeconds).Validate();
    }
}

public sealed class RelayIdentity(IConfiguration configuration)
{
    private readonly Lazy<X509Certificate2> _certificate = new(() => LoadOrCreate(configuration));
    public X509Certificate2 Certificate => _certificate.Value;
    public string Fingerprint => FingerprintOf(Certificate);

    public static string FingerprintOf(X509Certificate2 certificate)
    {
        using var key = certificate.GetRSAPublicKey() ?? throw new CryptographicException("Relay identity must use RSA.");
        return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }

    private static X509Certificate2 LoadOrCreate(IConfiguration config)
    {
        var root = Path.GetFullPath(config["SecurityAssets:RootPath"] ?? "security-assets");
        var directory = Path.Combine(root, "relay");
        Directory.CreateDirectory(directory);
        SecurityAssetFiles.RestrictDirectory(directory);
        var path = Path.Combine(directory, "identity.pem");
        if (!File.Exists(path))
        {
            using var key = RSA.Create(3072);
            var request = new CertificateRequest("CN=MailServer relay identity", key,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([
                new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"),
                new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.2")], true));
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, cert.ExportCertificatePem() + Environment.NewLine +
                key.ExportPkcs8PrivateKeyPem() + Environment.NewLine);
            SecurityAssetFiles.RestrictFile(temp);
            try { File.Move(temp, path, overwrite: false); }
            catch (IOException) { File.Delete(temp); }
        }
        SecurityAssetFiles.RestrictFile(path);
        var certificate = X509Certificate2.CreateFromPemFile(path, path);
        if (!OperatingSystem.IsWindows()) return certificate;
        // Schannel requires a native key handle. Convert in memory; no certificate is installed.
        var imported = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        certificate.Dispose();
        return imported;
    }
}
