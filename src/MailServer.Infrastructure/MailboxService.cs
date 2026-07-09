using System.Data;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MailServer.Infrastructure;

public sealed class MailboxService(
    IDbContextFactory<MailDbContext> dbFactory,
    IMessageContentStore contentStore,
    SystemSettingsService systemSettings,
    SentCopyService sentCopies) : IMailboxService
{
    private static long _lastUidValidity = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> MailboxLocks = new();
    public MailboxService(
        IDbContextFactory<MailDbContext> dbFactory,
        IMessageContentStore contentStore,
        SystemSettingsService systemSettings)
        : this(
            dbFactory,
            contentStore,
            systemSettings,
            new SentCopyService(
                dbFactory,
                contentStore,
                Microsoft.Extensions.Options.Options.Create(new MailRulesOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SentCopyService>.Instance))
    {
    }

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

    public async Task EnsureDefaultFoldersAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var settings = await systemSettings.GetMailboxSettingsAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Mailboxes
            .Where(x => x.AccountId == accountId)
            .Select(x => x.Name)
            .ToListAsync(cancellationToken);
        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in settings.DefaultFolders)
        {
            var normalized = MailboxNames.Normalize(folder);
            if (seen.Add(normalized))
            {
                db.Mailboxes.Add(new Mailbox { AccountId = accountId, Name = normalized });
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<MailboxSummary?> SelectAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var mailbox = await FindMailboxAsync(db, accountId, MailboxNames.Normalize(mailboxName), cancellationToken);
        return mailbox is null ? null : await SummarizeAsync(db, mailbox, cancellationToken);
    }

    public async Task<MailboxSummary> CreateAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken)
    {
        var normalized = MailboxNames.Normalize(mailboxName);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await FindMailboxAsync(db, accountId, normalized, cancellationToken);
        if (existing is not null)
        {
            return await SummarizeAsync(db, existing, cancellationToken);
        }

        var mailbox = new Mailbox { AccountId = accountId, Name = normalized, UidValidity = NewUidValidity() };
        db.Mailboxes.Add(mailbox);
        await db.SaveChangesAsync(cancellationToken);
        return await SummarizeAsync(db, mailbox, cancellationToken);
    }

    public async Task DeleteAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken)
    {
        var normalized = MailboxNames.Normalize(mailboxName);
        if (normalized == "INBOX")
        {
            throw new InvalidOperationException("INBOX cannot be deleted.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var mailbox = await FindMailboxAsync(db, accountId, normalized, cancellationToken)
            ?? throw new KeyNotFoundException("Mailbox not found.");
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
            .Include(x => x.Message)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        var normalized = criteria.Trim().ToUpperInvariant();
        IEnumerable<(MailboxMessage Message, long Sequence)> selected = messages.Select((message, index) => (message, index + 1L));
        if (normalized == "ALL")
        {
            // ALL is already the default selected set.
        }
        else if (TryParseDateSearch(criteria, out var operatorName, out var date))
        {
            selected = operatorName switch
            {
                "SINCE" => selected.Where(x => x.Message.InternalDate.LocalDateTime.Date >= date),
                "BEFORE" => selected.Where(x => x.Message.InternalDate.LocalDateTime.Date < date),
                "ON" => selected.Where(x => x.Message.InternalDate.LocalDateTime.Date == date),
                _ => selected
            };
        }
        else if (TryParseSubjectSearch(criteria, out var charset, out var subjectNeedle))
        {
            if (!IsSupportedSearchCharset(charset))
            {
                throw new InvalidOperationException($"Unsupported search charset: {charset}.");
            }

            var normalizedNeedle = NormalizeSearchText(subjectNeedle);
            var matches = new List<(MailboxMessage Message, long Sequence)>();
            foreach (var item in selected)
            {
                await using var content = await contentStore.OpenReadAsync(item.Message.Message.ContentKey, cancellationToken);
                var message = await MimeMessage.LoadAsync(content, cancellationToken);
                if (NormalizeSearchText(message.Subject ?? string.Empty).Contains(normalizedNeedle, StringComparison.Ordinal))
                {
                    matches.Add(item);
                }
            }

            selected = matches;
        }
        else
        {
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
        }
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
        var mailboxLock = MailboxLocks.GetOrAdd(mailboxId, _ => new SemaphoreSlim(1, 1));
        await mailboxLock.WaitAsync(cancellationToken);
        try
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
        finally
        {
            mailboxLock.Release();
        }
    }

    public async Task MarkSeenAsync(
        Guid mailboxId,
        IReadOnlyCollection<Guid> mailboxMessageIds,
        CancellationToken cancellationToken)
    {
        if (mailboxMessageIds.Count == 0)
        {
            return;
        }

        await StoreFlagsForIdsAsync(mailboxId, mailboxMessageIds, ["\\Seen"], cancellationToken);
    }

    public async Task<IReadOnlyList<ExpungedMailboxItem>> ExpungeAsync(Guid mailboxId, CancellationToken cancellationToken)
    {
        var mailboxLock = MailboxLocks.GetOrAdd(mailboxId, _ => new SemaphoreSlim(1, 1));
        await mailboxLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var messages = await db.MailboxMessages
                .Where(x => x.MailboxId == mailboxId)
                .OrderBy(x => x.Uid)
                .ToListAsync(cancellationToken);
            var expunged = messages
                .Select((message, index) => new { Message = message, Sequence = index + 1L })
                .Where(x => HasFlag(x.Message.Flags, "\\Deleted"))
                .Select(x => new ExpungedMailboxItem(x.Sequence, x.Message.Uid))
                .ToArray();
            db.MailboxMessages.RemoveRange(messages.Where(x => HasFlag(x.Flags, "\\Deleted")));
            await db.SaveChangesAsync(cancellationToken);
            return expunged;
        }
        finally
        {
            mailboxLock.Release();
        }
    }

    public async Task<CopyMoveResult> CopyAsync(
        Guid mailboxId,
        string sequenceSet,
        bool useUid,
        string destination,
        bool move,
        CancellationToken cancellationToken)
    {
        var mailboxLock = MailboxLocks.GetOrAdd(mailboxId, _ => new SemaphoreSlim(1, 1));
        await mailboxLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var source = await db.Mailboxes.SingleAsync(x => x.Id == mailboxId, cancellationToken);
            var target = await FindMailboxAsync(db, source.AccountId, MailboxNames.Normalize(destination), cancellationToken)
                ?? throw new KeyNotFoundException("Destination mailbox does not exist.");
            var messages = await db.MailboxMessages
                .Where(x => x.MailboxId == mailboxId)
                .OrderBy(x => x.Uid)
                .ToListAsync(cancellationToken);
            var selected = SelectMessages(messages, sequenceSet, useUid).ToArray();
            var selectedIds = selected.Select(x => x.Id).ToHashSet();
            var sourceUids = new List<long>();
            var targetUids = new List<long>();
            foreach (var message in messages.Where(x => selectedIds.Contains(x.Id)))
            {
                var targetUid = target.UidNext++;
                sourceUids.Add(message.Uid);
                targetUids.Add(targetUid);
                db.MailboxMessages.Add(new MailboxMessage
                {
                    MailboxId = target.Id,
                    MessageId = message.MessageId,
                    Uid = targetUid,
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
            var expunged = move
                ? selected.Select(x => new ExpungedMailboxItem(x.Sequence, x.Uid)).ToArray()
                : [];
            return new CopyMoveResult(source.UidValidity, target.UidValidity, sourceUids, targetUids, expunged);
        }
        finally
        {
            mailboxLock.Release();
        }
    }

    public async Task<MailboxItem> AppendAsync(
        Guid accountId,
        string mailboxName,
        Stream content,
        IReadOnlyCollection<string> flags,
        DateTimeOffset? internalDate,
        CancellationToken cancellationToken)
        => await sentCopies.AppendAsync(
            accountId,
            mailboxName,
            content,
            flags,
            internalDate,
            cancellationToken);

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

    private static async Task<Mailbox?> FindMailboxAsync(
        MailDbContext db,
        Guid accountId,
        string normalizedName,
        CancellationToken cancellationToken)
    {
        var mailboxes = await db.Mailboxes
            .Where(x => x.AccountId == accountId)
            .ToListAsync(cancellationToken);
        return mailboxes.SingleOrDefault(x => MailboxNames.Equals(x.Name, normalizedName));
    }

    private static long NewUidValidity()
    {
        while (true)
        {
            var current = Volatile.Read(ref _lastUidValidity);
            var next = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), current + 1);
            if (Interlocked.CompareExchange(ref _lastUidValidity, next, current) == current)
            {
                return next;
            }
        }
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

    private async Task StoreFlagsForIdsAsync(
        Guid mailboxId,
        IReadOnlyCollection<Guid> mailboxMessageIds,
        IReadOnlyCollection<string> flags,
        CancellationToken cancellationToken)
    {
        var mailboxLock = MailboxLocks.GetOrAdd(mailboxId, _ => new SemaphoreSlim(1, 1));
        await mailboxLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var mailbox = await db.Mailboxes.SingleAsync(x => x.Id == mailboxId, cancellationToken);
            var selectedIds = mailboxMessageIds.ToHashSet();
            var messages = await db.MailboxMessages
                .Where(x => x.MailboxId == mailboxId && selectedIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
            foreach (var message in messages)
            {
                var current = ParseFlags(message.Flags);
                var before = current.Count;
                current.UnionWith(flags);
                if (current.Count != before)
                {
                    message.Flags = string.Join(' ', current.Order(StringComparer.OrdinalIgnoreCase));
                    message.ModSeq = ++mailbox.HighestModSeq;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            mailboxLock.Release();
        }
    }

    private static bool TryParseDateSearch(string criteria, out string operatorName, out DateTime date)
    {
        var tokens = criteria.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        operatorName = tokens.Length == 2 ? tokens[0].ToUpperInvariant() : string.Empty;
        date = default;
        return tokens.Length == 2
               && (operatorName is "SINCE" or "BEFORE" or "ON")
               && DateTime.TryParseExact(
                   tokens[1],
                   "d-MMM-yyyy",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces,
                   out date);
    }

    private static bool TryParseSubjectSearch(string criteria, out string charset, out string needle)
    {
        var tokens = TokenizeSearchCriteria(criteria);
        charset = "UTF-8";
        needle = string.Empty;
        if (tokens.Count == 2 && tokens[0].Equals("SUBJECT", StringComparison.OrdinalIgnoreCase))
        {
            needle = tokens[1];
            return true;
        }

        if (tokens.Count == 4
            && tokens[0].Equals("CHARSET", StringComparison.OrdinalIgnoreCase)
            && tokens[2].Equals("SUBJECT", StringComparison.OrdinalIgnoreCase))
        {
            charset = tokens[1];
            needle = tokens[3];
            return true;
        }

        return false;
    }

    private static bool IsSupportedSearchCharset(string charset) =>
        charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase)
        || charset.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSearchText(string value)
    {
        var compatibility = value.Normalize(NormalizationForm.FormKC).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(compatibility.Length);
        foreach (var character in compatibility)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    private static IReadOnlyList<string> TokenizeSearchCriteria(string criteria)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var escaped = false;
        foreach (var character in criteria)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }

            if (quoted && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
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
