using System.Data;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MailServer.Infrastructure;

// Domain quota rows are the first locks for every participating mailbox writer.
internal static class MailboxWriteCoordinator
{
    public static async Task LockDomainsAsync(MailDbContext db, IEnumerable<Guid> domainIds, CancellationToken token)
    {
        if (db.Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) != true) return;
        foreach (var id in domainIds.Distinct().Order())
        {
            var current = (await db.Domains.FromSqlInterpolated($"SELECT * FROM mail_domains WHERE Id = {id} FOR UPDATE")
                .IgnoreQueryFilters().AsNoTracking().ToListAsync(token)).SingleOrDefault();
            if (current is null) continue;
            var entry = db.ChangeTracker.Entries<MailDomain>().SingleOrDefault(x => x.Entity.Id == id);
            if (entry is not null)
            {
                entry.CurrentValues.SetValues(current); entry.OriginalValues.SetValues(current);
                entry.State = EntityState.Unchanged;
            }
        }
    }

    public static async Task<IDbContextTransaction> BeginForMailboxAsync(MailDbContext db, Guid mailboxId, CancellationToken token)
    {
        var domainId = await db.Mailboxes.AsNoTracking().Where(x => x.Id == mailboxId)
            .Select(x => x.Account.DomainId).SingleAsync(token);
        return await BeginAsync(db, [domainId], token);
    }

    public static async Task<IDbContextTransaction> BeginForAccountAsync(MailDbContext db, Guid accountId, CancellationToken token)
    {
        var domainId = await db.Accounts.AsNoTracking().Where(x => x.Id == accountId)
            .Select(x => x.DomainId).SingleAsync(token);
        return await BeginAsync(db, [domainId], token);
    }

    public static async Task<IDbContextTransaction> BeginAsync(MailDbContext db, IEnumerable<Guid> domains, CancellationToken token)
    {
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        try { await LockDomainsAsync(db, domains, token); return transaction; }
        catch { await transaction.DisposeAsync(); throw; }
    }
}
