using MailServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Infrastructure;

public sealed class SmtpSenderAuthorizer(IDbContextFactory<MailDbContext> dbFactory, ConfigurationSnapshotCache? cache = null) : ISmtpSenderAuthorizer
{
    public async Task<bool> IsAuthorizedSenderAsync(
        long accountId,
        string sender,
        CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(sender, out var senderLocalPart, out var senderDomain))
        {
            return false;
        }

        async Task<(Authorization Value, long Size)> LoadAsync(CancellationToken token) =>
            (new Authorization(await IsAuthorizedCoreAsync(accountId, senderLocalPart, senderDomain, token)), 256);
        return cache is null ? (await LoadAsync(cancellationToken)).Value.Allowed
            : (await cache.GetAsync($"sender:{accountId}:{senderLocalPart}@{senderDomain}",
                ConfigurationScopes.Routing, LoadAsync, cancellationToken, TimeSpan.FromSeconds(10))).Allowed;
    }

    private sealed record Authorization(bool Allowed);
    private async Task<bool> IsAuthorizedCoreAsync(long accountId, string senderLocalPart, string senderDomain, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts
            .AsNoTracking()
            .Include(x => x.Domain)
            .SingleOrDefaultAsync(
                x => x.Id == accountId && x.IsEnabled && x.Domain.IsActive,
                cancellationToken);
        if (account is null)
        {
            return false;
        }

        if (senderLocalPart == account.LocalPart && senderDomain == account.Domain.Name)
        {
            return true;
        }

        var alias = await db.Aliases
            .AsNoTracking()
            .AnyAsync(
                x => x.SourceLocalPart == senderLocalPart
                     && x.Domain.Name == senderDomain
                     && x.Domain.IsActive
                     && x.IsEnabled
                     && x.Mode == MailAliasMode.SendReceive
                     && x.TargetAccountId == account.Id,
                cancellationToken);
        return alias;
    }
}
