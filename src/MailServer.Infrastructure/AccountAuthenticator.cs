using MailServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Infrastructure;

public sealed class AccountAuthenticator(
    IDbContextFactory<MailDbContext> dbFactory,
    ICredentialService credentials) : IAccountAuthenticator
{
    public async Task<Guid?> AuthenticateMailboxAsync(string address, string password, CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(address, out var localPart, out var domain))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts
            .Include(x => x.Domain)
            .SingleOrDefaultAsync(
                x => x.LocalPart == localPart && x.Domain.Name == domain && x.IsEnabled && x.Domain.IsActive,
                cancellationToken);
        if (account is null || !credentials.VerifyPassword(account.PasswordHash, password, out var upgradedHash))
        {
            return null;
        }

        if (upgradedHash is not null)
        {
            account.PasswordHash = upgradedHash;
            await db.SaveChangesAsync(cancellationToken);
        }

        return account.Id;
    }

    public async Task<Guid?> ResolveMailboxIdentityAsync(string address, CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(address, out var localPart, out var domain)) return null;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Accounts
            .Where(x => x.LocalPart == localPart && x.Domain.Name == domain && x.IsEnabled && x.Domain.IsActive)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }
}

internal static class AddressParser
{
    public static bool TrySplit(string address, out string localPart, out string domain)
    {
        var normalized = address.Trim().Trim('<', '>');
        var index = normalized.LastIndexOf('@');
        if (index <= 0 || index == normalized.Length - 1 || normalized.Length > 320)
        {
            localPart = string.Empty;
            domain = string.Empty;
            return false;
        }

        localPart = normalized[..index].ToLowerInvariant();
        domain = normalized[(index + 1)..].TrimEnd('.').ToLowerInvariant();
        return localPart.Length <= 128 && domain.Length <= 255;
    }

    public static string Normalize(string address)
    {
        if (!TrySplit(address, out var localPart, out var domain))
        {
            throw new FormatException($"Invalid email address: {address}");
        }

        return $"{localPart}@{domain}";
    }
}
