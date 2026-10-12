using MailServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Infrastructure;

internal enum QuotaAvailability { Available, AccountExceeded, DomainExceeded }

// Mail delivery, copy, restore and cleanup share these accounting primitives.
// Callers keep their existing database transactions as the cross-process source of truth.
internal static class MailboxWriteAccounting
{
    public static QuotaAvailability CheckQuota(MailAccount account, long bytes)
    {
        if (bytes < 0) return QuotaAvailability.Available;
        if (bytes > account.QuotaBytes - account.UsedBytes) return QuotaAvailability.AccountExceeded;
        if (bytes > account.Domain.QuotaBytes - account.Domain.UsedBytes) return QuotaAvailability.DomainExceeded;
        return QuotaAvailability.Available;
    }

    public static void ApplyQuotaDelta(MailAccount account, long delta) =>
        ApplyQuotaDelta(account, account.Domain, delta);

    public static void ApplyQuotaDelta(MailAccount account, MailDomain domain, long delta)
    {
        account.UsedBytes = Math.Max(0, checked(account.UsedBytes + delta));
        domain.UsedBytes = Math.Max(0, checked(domain.UsedBytes + delta));
    }

    public static async Task EnsureUidNextAsync(MailDbContext db, Mailbox mailbox, CancellationToken token)
    {
        // New mailboxes have no persisted UIDs. Existing legacy/imported counters
        // are repaired with the (MailboxId, Uid) index rather than an aggregate scan.
        if (db.Entry(mailbox).State == EntityState.Added)
        {
            mailbox.UidNext = Math.Max(1, mailbox.UidNext);
            return;
        }
        // Deleted UIDs are also allocated history and must never be reused.
        var highest = await db.MailboxMessages.IgnoreQueryFilters()
            .Where(x => x.MailboxId == mailbox.Id).OrderByDescending(x => x.Uid)
            .Select(x => (long?)x.Uid).FirstOrDefaultAsync(token);
        var next = checked((highest ?? 0) + 1);
        if (mailbox.UidNext < next) mailbox.UidNext = next;
    }

    public static long TakeUid(Mailbox mailbox) => mailbox.UidNext++;
}
