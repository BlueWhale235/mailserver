using MailServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Infrastructure;

public sealed class SmtpSenderAuthorizer(IDbContextFactory<MailDbContext> dbFactory) : ISmtpSenderAuthorizer
{
    public async Task<bool> IsAuthorizedSenderAsync(
        Guid accountId,
        string sender,
        CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(sender, out var senderLocalPart, out var senderDomain))
        {
            return false;
        }

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
