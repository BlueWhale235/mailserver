using System.Data;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Infrastructure;

public sealed class MailboxService(
    IDbContextFactory<MailDbContext> dbFactory,
    IMessageContentStore contentStore) : IMailboxService
{
    public async Task<IReadOnlyList<MailboxSummary>> ListAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var mailboxes = await db.Mailboxes
            .Where(x => x.AccountId == accountId)
            .OrderBy(x => x.Name == "INBOX" ? 0 : 1)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var result = new List<MailboxSummary>(mailboxes.Count);
        foreach (var mailbox in mailboxes)
        {
            result.Add(await SummarizeAsync(db, mailbox, cancellationToken));
        }

        return result;
    }

    public async Task<MailboxSummary?> SelectAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var mailbox = await db.Mailboxes.SingleOrDefaultAsync(
            x => x.AccountId == accountId && x.Name == NormalizeMailbox(mailboxName),
            cancellationToken);
        return mailbox is null ? null : await SummarizeAsync(db, mailbox, cancellationToken);
    }

    public async Task<MailboxSummary> CreateAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken)
    {
        var normalized = NormalizeMailbox(mailboxName);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Mailboxes.SingleOrDefaultAsync(
            x => x.AccountId == accountId && x.Name == normalized,
            cancellationToken);
        if (existing is not null)
        {
            return await SummarizeAsync(db, existing, cancellationToken);
        }

        var mailbox = new Mailbox { AccountId = accountId, Name = normalized };
        db.Mailboxes.Add(mailbox);
        await db.SaveChangesAsync(cancellationToken);
        return await SummarizeAsync(db, mailbox, cancellationToken);
    }

    public async Task DeleteAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken)
    {
        var normalized = NormalizeMailbox(mailboxName);
        if (normalized == "INBOX")
        {
            throw new InvalidOperationException("INBOX cannot be deleted.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var mailbox = await db.Mailboxes.SingleOrDefaultAsync(
            x => x.AccountId == accountId && x.Name == normalized,
            cancellationToken) ?? throw new KeyNotFoundException("Mailbox not found.");
        db.Mailboxes.Remove(mailbox);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MailboxItem>> FetchAsync(
        Guid mailboxId,
        string sequenceSet,
        bool useUid,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var messages = await db.MailboxMessages
            .Where(x => x.MailboxId == mailboxId)
            .Include(x => x.Message)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        return SelectMessages(messages, sequenceSet, useUid).ToArray();
    }

    public async Task<IReadOnlyList<long>> SearchAsync(
        Guid mailboxId,
        string criteria,
        bool useUid,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var messages = await db.MailboxMessages
            .Where(x => x.MailboxId == mailboxId)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        var normalized = criteria.Trim().ToUpperInvariant();
        IEnumerable<(MailboxMessage Message, long Sequence)> selected = messages.Select((message, index) => (message, index + 1L));
        selected = normalized switch
        {
            "SEEN" => selected.Where(x => HasFlag(x.Message.Flags, "\\Seen")),
            "UNSEEN" => selected.Where(x => !HasFlag(x.Message.Flags, "\\Seen")),
            "DELETED" => selected.Where(x => HasFlag(x.Message.Flags, "\\Deleted")),
            "UNDELETED" => selected.Where(x => !HasFlag(x.Message.Flags, "\\Deleted")),
            "FLAGGED" => selected.Where(x => HasFlag(x.Message.Flags, "\\Flagged")),
            "UNFLAGGED" => selected.Where(x => !HasFlag(x.Message.Flags, "\\Flagged")),
            "ANSWERED" => selected.Where(x => HasFlag(x.Message.Flags, "\\Answered")),
            "UNANSWERED" => selected.Where(x => !HasFlag(x.Message.Flags, "\\Answered")),
            _ => selected
        };
        return selected.Select(x => useUid ? x.Message.Uid : x.Sequence).ToArray();
    }

    public async Task StoreFlagsAsync(
        Guid mailboxId,
        string sequenceSet,
        bool useUid,
        string operation,
        IReadOnlyCollection<string> flags,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var mailbox = await db.Mailboxes.SingleAsync(x => x.Id == mailboxId, cancellationToken);
        var messages = await db.MailboxMessages
            .Where(x => x.MailboxId == mailboxId)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        var selectedIds = SelectMessages(messages, sequenceSet, useUid).Select(x => x.Id).ToHashSet();
        foreach (var message in messages.Where(x => selectedIds.Contains(x.Id)))
        {
            var current = ParseFlags(message.Flags);
            if (operation.StartsWith('+'))
            {
                current.UnionWith(flags);
            }
            else if (operation.StartsWith('-'))
            {
                current.ExceptWith(flags);
            }
            else
            {
                current = new HashSet<string>(flags, StringComparer.OrdinalIgnoreCase);
            }

            message.Flags = string.Join(' ', current.Order(StringComparer.OrdinalIgnoreCase));
            message.ModSeq = ++mailbox.HighestModSeq;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ExpungeAsync(Guid mailboxId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var messages = await db.MailboxMessages
            .Where(x => x.MailboxId == mailboxId)
            .ToListAsync(cancellationToken);
        db.MailboxMessages.RemoveRange(messages.Where(x => HasFlag(x.Flags, "\\Deleted")));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CopyAsync(
        Guid mailboxId,
        string sequenceSet,
        bool useUid,
        string destination,
        bool move,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var source = await db.Mailboxes.SingleAsync(x => x.Id == mailboxId, cancellationToken);
        var target = await db.Mailboxes.SingleOrDefaultAsync(
            x => x.AccountId == source.AccountId && x.Name == NormalizeMailbox(destination),
            cancellationToken) ?? throw new KeyNotFoundException("Destination mailbox does not exist.");
        var messages = await db.MailboxMessages
            .Where(x => x.MailboxId == mailboxId)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        var selectedIds = SelectMessages(messages, sequenceSet, useUid).Select(x => x.Id).ToHashSet();
        foreach (var message in messages.Where(x => selectedIds.Contains(x.Id)))
        {
            db.MailboxMessages.Add(new MailboxMessage
            {
                MailboxId = target.Id,
                MessageId = message.MessageId,
                Uid = target.UidNext++,
                ModSeq = ++target.HighestModSeq,
                Flags = message.Flags,
                InternalDate = message.InternalDate
            });
            if (move)
            {
                db.MailboxMessages.Remove(message);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MailboxItem> AppendAsync(
        Guid accountId,
        string mailboxName,
        Stream content,
        IReadOnlyCollection<string> flags,
        DateTimeOffset? internalDate,
        CancellationToken cancellationToken)
    {
        var descriptor = await contentStore.SaveAsync(content, cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var mailbox = await db.Mailboxes.SingleAsync(
                x => x.AccountId == accountId && x.Name == NormalizeMailbox(mailboxName),
                cancellationToken);
            var stored = new StoredMessage
            {
                ContentKey = descriptor.Key,
                EnvelopeFrom = string.Empty,
                Size = descriptor.Size
            };
            var item = new MailboxMessage
            {
                Mailbox = mailbox,
                Message = stored,
                Uid = mailbox.UidNext++,
                ModSeq = ++mailbox.HighestModSeq,
                Flags = string.Join(' ', flags),
                InternalDate = internalDate ?? DateTimeOffset.UtcNow
            };
            db.Messages.Add(stored);
            db.MailboxMessages.Add(item);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new MailboxItem(item.Id, 0, item.Uid, item.Flags, item.ModSeq, item.InternalDate, stored.Size, stored.ContentKey);
        }
        catch
        {
            await contentStore.DeleteIfUnreferencedAsync(descriptor.Key, cancellationToken);
            throw;
        }
    }

    public async Task<Stream> OpenMessageAsync(Guid mailboxMessageId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var key = await db.MailboxMessages
            .Where(x => x.Id == mailboxMessageId)
            .Select(x => x.Message.ContentKey)
            .SingleAsync(cancellationToken);
        return await contentStore.OpenReadAsync(key, cancellationToken);
    }

    private static async Task<MailboxSummary> SummarizeAsync(
        MailDbContext db,
        Mailbox mailbox,
        CancellationToken cancellationToken)
    {
        var messages = db.MailboxMessages.Where(x => x.MailboxId == mailbox.Id);
        var exists = await messages.LongCountAsync(cancellationToken);
        var flags = await messages.Select(x => x.Flags).ToListAsync(cancellationToken);
        var unseen = flags.LongCount(x => !HasFlag(x, "\\Seen"));
        return new MailboxSummary(mailbox.Id, mailbox.Name, exists, unseen, mailbox.UidValidity, mailbox.UidNext, mailbox.HighestModSeq);
    }

    private static IEnumerable<MailboxItem> SelectMessages(
        IReadOnlyList<MailboxMessage> messages,
        string sequenceSet,
        bool useUid)
    {
        var maximum = useUid ? messages.Select(x => x.Uid).DefaultIfEmpty(0).Max() : messages.Count;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var sequence = index + 1L;
            var value = useUid ? message.Uid : sequence;
            if (SequenceSet.Contains(sequenceSet, value, maximum))
            {
                yield return new MailboxItem(
                    message.Id,
                    sequence,
                    message.Uid,
                    message.Flags,
                    message.ModSeq,
                    message.InternalDate,
                    message.Message?.Size ?? 0,
                    message.Message?.ContentKey ?? string.Empty);
            }
        }
    }

    private static HashSet<string> ParseFlags(string flags) =>
        new(flags.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    private static bool HasFlag(string flags, string flag) => ParseFlags(flags).Contains(flag);

    private static string NormalizeMailbox(string name)
    {
        var value = name.Trim().Trim('"').Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value) || value.Contains("..", StringComparison.Ordinal) || value.Length > 255)
        {
            throw new FormatException("Invalid mailbox name.");
        }

        return value.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? "INBOX" : value;
    }
}

internal static class SequenceSet
{
    public static bool Contains(string expression, long value, long maximum)
    {
        foreach (var segment in expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = segment.Split(':', 2);
            var start = Parse(range[0], maximum);
            var end = range.Length == 1 ? start : Parse(range[1], maximum);
            if (value >= Math.Min(start, end) && value <= Math.Max(start, end))
            {
                return true;
            }
        }

        return false;
    }

    private static long Parse(string value, long maximum) =>
        value == "*" ? maximum : long.TryParse(value, out var parsed) ? parsed : -1;
}
