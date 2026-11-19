using System.Security.Cryptography;
using System.Text;
using MailServer.Domain;
using Microsoft.AspNetCore.Identity;

namespace MailServer.Infrastructure;

public sealed class CredentialService : ICredentialService, IDisposable
{
    private readonly PasswordHasher<object> _passwordHasher = new();
    private static readonly object User = new();
    private static readonly SemaphoreSlim VerificationSlots = new(Math.Max(1, Environment.ProcessorCount));
    private readonly Microsoft.Extensions.Caching.Memory.MemoryCache _verified = new(
        new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 4096 });
    private readonly byte[] _cacheKey = RandomNumberGenerator.GetBytes(32);

    public string HashPassword(string password) => _passwordHasher.HashPassword(User, password);

    public bool VerifyPassword(string hash, string password, out string? upgradedHash)
    {
        // A changed hash (including a password reset) cannot hit an old success. Only
        // successful checks are cached, and passwords are keyed with a process secret.
        var bytes = Encoding.UTF8.GetBytes(password);
        var key = (hash, Convert.ToHexString(HMACSHA256.HashData(_cacheKey, bytes)));
        CryptographicOperations.ZeroMemory(bytes);
        upgradedHash = null;
        if (_verified.TryGetValue(key, out _)) return true;
        VerificationSlots.Wait();
        try
        {
            if (_verified.TryGetValue(key, out _)) return true;
            var result = _passwordHasher.VerifyHashedPassword(User, hash, password);
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
                upgradedHash = HashPassword(password);
            if (result == PasswordVerificationResult.Success)
            {
                using var entry = _verified.CreateEntry(key);
                entry.Value = true;
                entry.Size = 1;
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(90);
            }
            return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
        }
        finally { VerificationSlots.Release(); }
    }

    public string HashApiSecret(string secret)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hash);
    }

    public bool VerifyApiSecret(string hash, string secret)
    {
        var expected = Convert.FromHexString(hash);
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public void Dispose()
    {
        _verified.Dispose();
        CryptographicOperations.ZeroMemory(_cacheKey);
    }
}
