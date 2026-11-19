using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MailServer.Infrastructure;

/// <summary>Shared accounting for every tracked insert, flag change, move, recycle and restore.</summary>
internal static class MailboxPersistence
{
    private sealed record QuotaChange(EntityEntry Entry, long Id, long Delta, long Desired);

    internal static async Task<int> SaveAsync(MailDbContext db, Func<CancellationToken, Task<int>> save, CancellationToken token)
    {
        db.ChangeTracker.DetectChanges();
        var autoDetect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try { return await SaveCoreAsync(db, save, token); }
        finally { db.ChangeTracker.AutoDetectChangesEnabled = autoDetect; }
    }

    private static async Task<int> SaveCoreAsync(MailDbContext db, Func<CancellationToken, Task<int>> save, CancellationToken token)
    {
        // Importers may provide UIDs, but no persisted writer may move UIDNEXT backwards.
        foreach (var entry in db.ChangeTracker.Entries<Mailbox>().Where(x => x.State == EntityState.Modified))
            entry.Entity.UidNext = Math.Max(entry.Entity.UidNext, entry.Property(x => x.UidNext).OriginalValue);
        var allocated = db.ChangeTracker.Entries<MailboxMessage>().Where(x => x.State == EntityState.Added)
            .GroupBy(x => x.Entity.MailboxId).ToDictionary(x => x.Key, x => checked(x.Max(y => y.Entity.Uid) + 1));
        foreach (var pair in allocated)
        {
            var mailbox = db.Mailboxes.Local.FirstOrDefault(x => x.Id == pair.Key)
                ?? await db.Mailboxes.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == pair.Key, token);
            if (mailbox is not null) mailbox.UidNext = Math.Max(mailbox.UidNext, pair.Value);
        }
        var counts = new Dictionary<long, (long Exists, long Unseen)>();
        void Add(long id, string flags, long delta)
        {
            var previous = counts.GetValueOrDefault(id);
            var seen = flags.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("\\Seen", StringComparer.OrdinalIgnoreCase);
            counts[id] = (previous.Exists + delta, previous.Unseen + (seen ? 0 : delta));
        }
        foreach (var entry in db.ChangeTracker.Entries<MailboxMessage>())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached) continue;
            if (entry.State != EntityState.Added && entry.OriginalValues.GetValue<DateTimeOffset?>(nameof(MailboxMessage.DeletedAt)) is null)
                Add(entry.OriginalValues.GetValue<long>(nameof(MailboxMessage.MailboxId)), entry.OriginalValues.GetValue<string>(nameof(MailboxMessage.Flags)), -1);
            if (entry.State != EntityState.Deleted && entry.Entity.DeletedAt is null)
                Add(entry.Entity.MailboxId, entry.Entity.Flags, 1);
        }
        var deltas = counts.Where(x => x.Value != (0L, 0L)).OrderBy(x => x.Key).ToArray();
        var quotas = db.AtomicQuotaUpdates && db.Database.IsRelational()
            ? db.ChangeTracker.Entries().Where(x => x.State == EntityState.Modified && x.Entity is MailAccount or MailDomain)
                .Where(x => x.Property("UsedBytes").IsModified)
                .Select(x => new QuotaChange(x, (long)x.Property("Id").CurrentValue!,
                    checked((long)x.Property("UsedBytes").CurrentValue! - (long)x.Property("UsedBytes").OriginalValue!),
                    (long)x.Property("UsedBytes").CurrentValue!)).Where(x => x.Delta != 0).ToArray()
            : [];
        if (deltas.Length == 0 && quotas.Length == 0) return await save(token);

        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        foreach (var change in quotas) change.Entry.Property("UsedBytes").IsModified = false;
        if (!db.Database.IsRelational())
        {
            foreach (var delta in deltas)
            {
                var mailbox = db.Mailboxes.Local.FirstOrDefault(x => x.Id == delta.Key)
                    ?? await db.Mailboxes.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == delta.Key, token);
                if (mailbox is null) continue;
                mailbox.MessageCount += delta.Value.Exists;
                mailbox.UnseenCount += delta.Value.Unseen;
            }
        }
        var saved = await save(token);
        if (db.Database.IsRelational())
        {
            foreach (var delta in deltas)
            {
                var exists = delta.Value.Exists;
                var unseen = delta.Value.Unseen;
                await db.Mailboxes.IgnoreQueryFilters().Where(x => x.Id == delta.Key).ExecuteUpdateAsync(
                    set => set.SetProperty(x => x.MessageCount, x => x.MessageCount + exists)
                        .SetProperty(x => x.UnseenCount, x => x.UnseenCount + unseen), token);
                var entry = db.ChangeTracker.Entries<Mailbox>().FirstOrDefault(x => x.Entity.Id == delta.Key);
                if (entry is null) continue;
                entry.Entity.MessageCount += exists;
                entry.Entity.UnseenCount += unseen;
                entry.Property(x => x.MessageCount).OriginalValue = entry.Entity.MessageCount;
                entry.Property(x => x.UnseenCount).OriginalValue = entry.Entity.UnseenCount;
            }
            // Every writer takes account rows, then domain rows, in ascending ID order.
            foreach (var change in quotas.OrderBy(x => x.Entry.Entity is MailDomain ? 1 : 0).ThenBy(x => x.Id))
            {
                var delta = change.Delta;
                if (change.Entry.Entity is MailAccount account)
                {
                    var changed = await db.Accounts.IgnoreQueryFilters()
                        .Where(x => x.Id == change.Id && (delta <= 0 || x.QuotaBytes - x.UsedBytes >= delta))
                        .ExecuteUpdateAsync(set => set.SetProperty(x => x.UsedBytes,
                            x => x.UsedBytes + delta < 0 ? 0 : x.UsedBytes + delta), token);
                    if (changed != 1) throw new MailboxQuotaExceededException($"{account.LocalPart}@{account.Domain.Name}");
                }
                else if (change.Entry.Entity is MailDomain domain)
                {
                    var changed = await db.Domains.IgnoreQueryFilters()
                        .Where(x => x.Id == change.Id && (delta <= 0 || x.QuotaBytes - x.UsedBytes >= delta))
                        .ExecuteUpdateAsync(set => set.SetProperty(x => x.UsedBytes,
                            x => x.UsedBytes + delta < 0 ? 0 : x.UsedBytes + delta), token);
                    if (changed != 1)
                    {
                        var affectedAccount = quotas.Select(x => x.Entry.Entity).OfType<MailAccount>().FirstOrDefault(x => x.DomainId == domain.Id);
                        throw new DomainQuotaExceededException(domain.Name, affectedAccount is null ? string.Empty : $"{affectedAccount.LocalPart}@{domain.Name}");
                    }
                }
                change.Entry.Property("UsedBytes").CurrentValue = change.Desired;
                change.Entry.Property("UsedBytes").OriginalValue = change.Desired;
                change.Entry.Property("UsedBytes").IsModified = false;
            }
        }
        if (transaction is not null) await transaction.CommitAsync(token);
        return saved;
    }
}
