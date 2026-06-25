using System.Security.Cryptography;
using System.Text;
using MailServer.Domain;
using Microsoft.AspNetCore.Identity;

namespace MailServer.Infrastructure;

public sealed class CredentialService : ICredentialService
{
    private readonly PasswordHasher<object> _passwordHasher = new();
    private static readonly object User = new();

    public string HashPassword(string password) => _passwordHasher.HashPassword(User, password);

    public bool VerifyPassword(string hash, string password, out string? upgradedHash)
    {
        var result = _passwordHasher.VerifyHashedPassword(User, hash, password);
        upgradedHash = result == PasswordVerificationResult.SuccessRehashNeeded
            ? HashPassword(password)
            : null;
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
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
}
