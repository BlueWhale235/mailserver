using System.Data;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MailServer.Infrastructure;

// Hot writers lock mailboxes; quota deltas are conditional updates at SaveChanges.
// Administrative operations still lock their full affected set for reconciliation/deletion.
internal static class MailboxWriteCoordinator
{
    public static async Task LockDomainsAsync(MailDbContext db, IEnumerable<long> domainIds, CancellationToken token)
    {
        if (db.Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) != true) return;
        var ids = domainIds.Distinct().Order().ToArray();
        var mailboxes = await db.Mailboxes.IgnoreQueryFilters().Where(x => ids.Contains(x.Account.DomainId))
            .Select(x => x.Id).ToArrayAsync(token);
        await LockMailboxesAsync(db, mailboxes, token);
        foreach (var id in ids)
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

    public static async Task<IDbContextTransaction> BeginForMailboxAsync(MailDbContext db, long mailboxId, CancellationToken token)
    {
        return await BeginForMailboxesAsync(db, [mailboxId], token);
    }

    public static async Task<IDbContextTransaction> BeginForAccountAsync(MailDbContext db, long accountId, CancellationToken token)
    {
        return await BeginForAccountsAsync(db, [accountId], token);
    }

    public static async Task<IDbContextTransaction> BeginForAccountsAsync(MailDbContext db, IEnumerable<long> accounts, CancellationToken token)
    {
        var ids = accounts.Distinct().ToArray();
        var mailboxIds = await db.Mailboxes.IgnoreQueryFilters().Where(x => ids.Contains(x.AccountId))
            .Select(x => x.Id).ToArrayAsync(token);
        return await BeginForMailboxesAsync(db, mailboxIds, token);
    }

    public static async Task<IDbContextTransaction> BeginForMailboxesAsync(MailDbContext db, IEnumerable<long> ids, CancellationToken token)
    {
        var transaction = await db.Database.BeginTransactionAsync(
            db.Database.IsSqlite() ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted, token);
        try
        {
            db.AtomicQuotaUpdates = true;
            await LockMailboxesAsync(db, ids, token);
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    private static async Task LockMailboxesAsync(MailDbContext db, IEnumerable<long> ids, CancellationToken token)
    {
        if (db.Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) != true) return;
        foreach (var id in ids.Distinct().Order())
        {
            var current = (await db.Mailboxes.FromSqlInterpolated($"SELECT * FROM mailboxes WHERE Id = {id} FOR UPDATE")
                .IgnoreQueryFilters().AsNoTracking().ToListAsync(token)).SingleOrDefault();
            var entry = db.ChangeTracker.Entries<Mailbox>().SingleOrDefault(x => x.Entity.Id == id);
            if (current is null || entry is null) continue;
            entry.CurrentValues.SetValues(current);
            entry.OriginalValues.SetValues(current);
            entry.State = EntityState.Unchanged;
        }
    }

    public static async Task<IDbContextTransaction> BeginAsync(MailDbContext db, IEnumerable<long> domains, CancellationToken token)
    {
        var transaction = await db.Database.BeginTransactionAsync(
            db.Database.IsSqlite() ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted, token);
        try { await LockDomainsAsync(db, domains, token); return transaction; }
        catch { await transaction.DisposeAsync(); throw; }
    }
}
