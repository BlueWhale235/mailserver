using System.Data;
using System.Security.Cryptography;
using System.Text;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailServer.Infrastructure;

public sealed class SentCopyService(
    IDbContextFactory<MailDbContext> dbFactory,
    IMessageContentStore contentStore,
    IOptions<MailRulesOptions> options,
    IClock clock,
    ILogger<SentCopyService> logger)
{
    private readonly TimeSpan _clientGrace = TimeSpan.FromSeconds(Math.Clamp(options.Value.SentCopyGraceSeconds, 5, 600));
    private readonly TimeSpan _deduplicationWindow = TimeSpan.FromMinutes(Math.Clamp(options.Value.SentCopyDeduplicationMinutes, 1, 1440));

    public SentCopyService(
        IDbContextFactory<MailDbContext> dbFactory,
        IMessageContentStore contentStore,
        IOptions<MailRulesOptions> options,
        ILogger<SentCopyService> logger)
        : this(dbFactory, contentStore, options, new SystemClock(), logger)
    {
    }

    public SentCopyJob Queue(
        MailDbContext db,
        MailAccount account,
        StoredMessage message,
        MimeMessage mimeMessage,
        string targetMailboxName,
        Guid? ruleId)
    {
        var now = clock.UtcNow;
        var job = new SentCopyJob
        {
            Account = account,
            Message = message,
            RuleId = ruleId,
            TargetMailboxName = MailboxNames.Normalize(targetMailboxName),
            IdentityKey = BuildIdentityKey(mimeMessage),
            ReadyAt = now.Add(_clientGrace),
            DeduplicateUntil = now.Add(_deduplicationWindow)
        };
        db.SentCopyJobs.Add(job);
        return job;
    }

    public async Task<AppendResult> AppendAsync(
        Guid accountId,
        string mailboxName,
        Stream content,
        IReadOnlyCollection<string> flags,
        DateTimeOffset? internalDate,
        CancellationToken cancellationToken)
    {
        var descriptor = await contentStore.SaveAsync(content, cancellationToken);
        MimeMessage? mimeMessage = null;
        try
        {
            await using var parseStream = await contentStore.OpenReadAsync(descriptor.Key, cancellationToken);
            mimeMessage = await MimeMessage.LoadAsync(parseStream, cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or IOException)
        {
            logger.LogDebug(exception, "IMAP APPEND content could not be parsed for sent-copy matching.");
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var normalizedMailboxName = MailboxNames.Normalize(mailboxName);
            var mailbox = await db.Mailboxes.Include(x => x.Account).ThenInclude(x => x.Domain).SingleAsync(
                x => x.AccountId == accountId && x.Name == normalizedMailboxName,
                cancellationToken);
            if (mailbox.PendingDeletionJobId is not null)
                throw new InvalidOperationException("Mailbox deletion is in progress; new messages cannot be appended.");
            await MailboxService.EnsureUidNextAtLeastAsync(db, mailbox, cancellationToken);
            SentCopyJob? matchingJob = null;
            if (MailboxNames.IsSent(normalizedMailboxName))
            {
                var now = clock.UtcNow;
                var identityKey = mimeMessage is null ? null : BuildIdentityKey(mimeMessage);
                var candidates = await db.SentCopyJobs
                    .Include(x => x.Message)
                    .Where(x => x.AccountId == accountId && x.DeduplicateUntil >= now)
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync(cancellationToken);
                matchingJob = candidates.FirstOrDefault(x => x.Message.ContentKey == descriptor.Key)
                              ?? (identityKey is null ? null : candidates.FirstOrDefault(x => x.IdentityKey == identityKey));
            }

            var stored = matchingJob?.Message.ContentKey == descriptor.Key
                ? matchingJob.Message
                : new StoredMessage
                {
                    ContentKey = descriptor.Key,
                    EnvelopeFrom = string.Empty,
                    InternetMessageId = mimeMessage?.MessageId,
                    InternetMessageIdHash = MessageIdLookup.Hash(mimeMessage?.MessageId),
                    SourceType = "imap-append",
                    AuthenticatedAccountId = accountId,
                    AuthenticatedAccountName = $"{mailbox.Account.LocalPart}@{mailbox.Account.Domain.Name}",
                    Size = descriptor.Size
                };
            if (stored.Id == default || db.Entry(stored).State == EntityState.Detached)
            {
                db.Messages.Add(stored);
            }

            long replacedBytes = 0;
            if (matchingJob?.ServerMailboxMessageId is { } oldServerId)
            {
                var oldServerCopy = await db.MailboxMessages.Include(x => x.Message).SingleOrDefaultAsync(x => x.Id == oldServerId, cancellationToken);
                if (oldServerCopy is not null)
                {
                    replacedBytes = oldServerCopy.Message.Size;
                    db.MailboxMessages.Remove(oldServerCopy);
                }
                matchingJob.ServerMailboxMessageId = null;
            }

            var item = new MailboxMessage
            {
                Mailbox = mailbox,
                Message = stored,
                Uid = mailbox.UidNext++,
                ModSeq = ++mailbox.HighestModSeq,
                Flags = string.Join(' ', flags),
                InternalDate = internalDate ?? clock.UtcNow
            };
            db.MailboxMessages.Add(item);
            var quotaDelta = descriptor.Size - replacedBytes;
            if (quotaDelta > 0
                && (mailbox.Account.UsedBytes + quotaDelta > mailbox.Account.QuotaBytes
                    || mailbox.Account.Domain.UsedBytes + quotaDelta > mailbox.Account.Domain.QuotaBytes))
                throw new InvalidOperationException("Mailbox or domain quota exceeded.");
            mailbox.Account.UsedBytes = Math.Max(0, mailbox.Account.UsedBytes + quotaDelta);
            mailbox.Account.Domain.UsedBytes = Math.Max(0, mailbox.Account.Domain.UsedBytes + quotaDelta);
            db.QuotaLedger.Add(new QuotaLedgerEntry
            {
                IdempotencyKey = $"mailbox-message:add:{item.Id:N}",
                AccountId = mailbox.AccountId,
                DomainId = mailbox.Account.DomainId,
                MailboxMessageId = item.Id,
                Operation = quotaDelta >= 0 ? QuotaLedgerOperation.Add : QuotaLedgerOperation.Remove,
                Bytes = quotaDelta
            });
            var uidValidity = mailbox.UidValidity;
            var uidNext = mailbox.UidNext;
            if (matchingJob is not null)
            {
                matchingJob.ClientMailboxMessageId = item.Id;
                matchingJob.State = string.Equals(matchingJob.TargetMailboxName, mailbox.Name, StringComparison.OrdinalIgnoreCase)
                    ? SentCopyState.Completed
                    : SentCopyState.MovePending;
                matchingJob.ReadyAt = clock.UtcNow.AddSeconds(2);
                matchingJob.CompletedAt = matchingJob.State == SentCopyState.Completed ? clock.UtcNow : null;
                logger.LogInformation(
                    "Matched IMAP sent copy to job {JobId}: accountId={AccountId}, contentMatch={ContentMatch}, targetMailbox={TargetMailbox}.",
                    matchingJob.Id,
                    accountId,
                    matchingJob.Message.ContentKey == descriptor.Key,
                    matchingJob.TargetMailboxName);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AppendResult(
                new MailboxItem(item.Id, 0, item.Uid, item.Flags, item.ModSeq, item.InternalDate, stored.Size, stored.ContentKey),
                uidValidity,
                uidNext);
        }
        catch
        {
            await contentStore.DeleteIfUnreferencedAsync(descriptor.Key, cancellationToken);
            throw;
        }
    }

    public async Task<int> ProcessDueAsync(int maximum, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using (var cleanupDb = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await cleanupDb.SentCopyJobs
                .IgnoreQueryFilters()
                .Where(x => x.State == SentCopyState.Completed && x.DeduplicateUntil < now)
                .ExecuteDeleteAsync(cancellationToken);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ids = await db.SentCopyJobs.AsNoTracking()
            .Where(x => x.State != SentCopyState.Completed && x.ReadyAt <= now)
            .OrderBy(x => x.ReadyAt)
            .Select(x => x.Id)
            .Take(Math.Clamp(maximum, 1, 100))
            .ToListAsync(cancellationToken);
        var processed = 0;
        foreach (var id in ids)
        {
            processed += await ProcessOneAsync(id, cancellationToken) ? 1 : 0;
        }

        return processed;
    }

    private async Task<bool> ProcessOneAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var claimDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var claimed = await claimDb.SentCopyJobs
            .Where(x => x.Id == jobId && x.State != SentCopyState.Completed && x.ReadyAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, SentCopyState.Processing)
                .SetProperty(x => x.ReadyAt, now.AddMinutes(2)), cancellationToken);
        if (claimed == 0) return false;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var job = await db.SentCopyJobs.Include(x => x.Message).Include(x => x.Account).ThenInclude(x => x.Domain)
            .SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null) return false;
        var target = await db.Mailboxes.SingleOrDefaultAsync(
            x => x.AccountId == job.AccountId && x.Name == job.TargetMailboxName && x.PendingDeletionJobId == null,
            cancellationToken);
        if (target is null)
        {
            target = await db.Mailboxes.SingleOrDefaultAsync(
                x => x.AccountId == job.AccountId && x.Name == "Sent",
                cancellationToken);
            if (target is null)
            {
                target = new Mailbox { AccountId = job.AccountId, Name = "Sent" };
                db.Mailboxes.Add(target);
            }

            logger.LogWarning(
                "Sent-copy target mailbox {TargetMailbox} does not exist for account {AccountId}; falling back to Sent.",
                job.TargetMailboxName,
                job.AccountId);
        }

        MailboxMessage? clientCopy = null;
        if (job.ClientMailboxMessageId is { } clientId)
        {
            clientCopy = await db.MailboxMessages.Include(x => x.Mailbox)
                .SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken);
        }

        if (clientCopy is not null && clientCopy.MailboxId == target.Id)
        {
            job.State = SentCopyState.Completed;
            job.CompletedAt = now;
        }
        else if (clientCopy is not null)
        {
            await MailboxService.EnsureUidNextAtLeastAsync(db, target, cancellationToken);
            var moved = CreateMailboxMessage(target, clientCopy.MessageId, clientCopy.Flags, clientCopy.InternalDate);
            db.MailboxMessages.Add(moved);
            db.MailboxMessages.Remove(clientCopy);
            job.ClientMailboxMessageId = moved.Id;
            job.State = SentCopyState.Completed;
            job.CompletedAt = now;
        }
        else
        {
            await MailboxService.EnsureUidNextAtLeastAsync(db, target, cancellationToken);
            var copy = CreateMailboxMessage(target, job.MessageId, string.Empty, job.Message.ReceivedAt);
            db.MailboxMessages.Add(copy);
            if (job.Account.UsedBytes + job.Message.Size > job.Account.QuotaBytes
                || job.Account.Domain.UsedBytes + job.Message.Size > job.Account.Domain.QuotaBytes)
                throw new InvalidOperationException("Mailbox or domain quota exceeded.");
            job.Account.UsedBytes += job.Message.Size;
            job.Account.Domain.UsedBytes += job.Message.Size;
            db.QuotaLedger.Add(new QuotaLedgerEntry
            {
                IdempotencyKey = $"mailbox-message:add:{copy.Id:N}",
                AccountId = job.AccountId,
                DomainId = job.Account.DomainId,
                MailboxMessageId = copy.Id,
                Operation = QuotaLedgerOperation.Add,
                Bytes = job.Message.Size
            });
            job.ServerMailboxMessageId = copy.Id;
            job.State = SentCopyState.Completed;
            job.CompletedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Sent-copy job {JobId} completed for account {AccountId}: targetMailbox={TargetMailbox}, clientProvided={ClientProvided}.",
            job.Id,
            job.AccountId,
            target.Name,
            clientCopy is not null);
        return true;
    }

    private static MailboxMessage CreateMailboxMessage(
        Mailbox mailbox,
        Guid messageId,
        string flags,
        DateTimeOffset internalDate) => new()
    {
        Mailbox = mailbox,
        MessageId = messageId,
        Uid = mailbox.UidNext++,
        ModSeq = ++mailbox.HighestModSeq,
        Flags = flags,
        InternalDate = internalDate
    };

    public static string? BuildIdentityKey(MimeMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId)) return null;
        var from = string.Join(',', message.From.Mailboxes.Select(x => AddressParser.Normalize(x.Address)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var value = $"{message.MessageId.Trim().ToLowerInvariant()}\n{from}\n{(message.Subject ?? string.Empty).Trim().ToLowerInvariant()}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
