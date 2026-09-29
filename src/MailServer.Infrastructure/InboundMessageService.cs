using System.Data;
using System.Net;
using System.Text;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailServer.Infrastructure;

/// <summary>
/// 入站邮件落库与分发服务。SMTP、插件发信和退信都会进入这里，保证规则、配额和审计路径一致。
/// </summary>
public sealed class InboundMessageService(
    IDbContextFactory<MailDbContext> dbFactory,
    IMessageContentStore contentStore,
    MailRuleService rules,
    SentCopyService sentCopies,
    SrsService srs,
    IOptions<ForwardingOptions> forwardingOptions,
    IMimeInspectionService mimeInspection,
    IClock clock,
    ILogger<InboundMessageService> logger,
    ConfigurationSnapshotCache? configurationCache = null) : IInboundMessageService
{
    private readonly ForwardingOptions _forwardingOptions = forwardingOptions.Value;
    private sealed record RecipientRoute(bool IsLocal);

    public InboundMessageService(
        IDbContextFactory<MailDbContext> dbFactory,
        IMessageContentStore contentStore,
        ILogger<InboundMessageService> logger)
        : this(
            dbFactory,
            contentStore,
            new MailRuleService(dbFactory, Microsoft.Extensions.Logging.Abstractions.NullLogger<MailRuleService>.Instance),
            new SentCopyService(
                dbFactory,
                contentStore,
                Microsoft.Extensions.Options.Options.Create(new MailRulesOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SentCopyService>.Instance),
            new SrsService(Microsoft.Extensions.Options.Options.Create(new ForwardingOptions())),
            Microsoft.Extensions.Options.Options.Create(new ForwardingOptions()),
            new MimeInspectionService(),
            new SystemClock(),
            logger)
    {
    }

    public async Task<bool> IsLocalRecipientAsync(string address, CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(address, out var localPart, out var domainName)) return false;
        // SRS 回写地址视为本地可接收，避免转发退信被当作外部中继拒绝。
        if (localPart.StartsWith("srs0=", StringComparison.OrdinalIgnoreCase))
        {
            await using var srsDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (await srs.TryDecodeAsync(srsDb, address, cancellationToken) is not null) return true;
        }
        async Task<(RecipientRoute Value, long Size)> LoadAsync(CancellationToken token)
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            var isLocal = await db.Accounts.AnyAsync(
                    x => x.LocalPart == localPart && x.Domain.Name == domainName && x.IsEnabled && x.Domain.IsActive,
                    token)
                || await db.Aliases.AnyAsync(
                    x => x.SourceLocalPart == localPart
                        && x.Domain.Name == domainName
                        && x.Domain.IsActive
                        && x.IsEnabled
                        && x.Mode != MailAliasMode.Disabled
                        && x.TargetAccountId != null
                        && x.TargetAccount!.IsEnabled
                        && x.TargetAccount.Domain.IsActive, token);
            return (new RecipientRoute(isLocal), 64 + address.Length * 2L);
        }
        if (configurationCache is null || localPart.StartsWith("srs0=", StringComparison.OrdinalIgnoreCase))
            return (await LoadAsync(cancellationToken)).Value.IsLocal;
        var route = await configurationCache.GetAsync(
            $"routing:{localPart.ToLowerInvariant()}@{domainName.ToLowerInvariant()}",
            ConfigurationScopes.Routing, LoadAsync, cancellationToken, TimeSpan.FromSeconds(10));
        return route.IsLocal;
    }

    public async Task<InboundAcceptance> AcceptAsync(
        InboundEnvelope envelope,
        Stream content,
        CancellationToken cancellationToken)
    {
        using var ruleEvaluation = MailRuleService.BeginEvaluationScope();
        using var performance = PerformanceTelemetry.Operation("smtp", "message.accept");
        logger.LogInformation(
            "Inbound message received from {RemoteAddress}: sender={Sender}, recipientCount={RecipientCount}, authenticatedUser={AuthenticatedUser}, helo={HeloName}.",
            envelope.RemoteAddress,
            envelope.Sender,
            envelope.Recipients.Count,
            envelope.AuthenticatedUser ?? string.Empty,
            envelope.HeloName);
        var descriptor = await contentStore.SaveAsync(content, cancellationToken);
        try
        {
            var mimeMessage = MailRuleService.GetParsedMessage(content)
                ?? await LoadMimeMessageWithFallbackAsync(descriptor.Key, cancellationToken);

            var receivedAt = clock.UtcNow;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            // 入站分发需要在一个可串行化事务内完成，防止别名展开、配额和队列写入之间出现竞态。
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AuditDuplicateInboundAsync(db, envelope, descriptor, mimeMessage, receivedAt, cancellationToken);
            await AuditLocalDomainSpoofAsync(db, envelope, cancellationToken);
            AuditDuplicateSingletonHeaders(db, envelope, descriptor, mimeMessage);
            await AuditMimeInspectionAsync(db, envelope, descriptor.Key, cancellationToken);
            var storedMessage = new StoredMessage
            {
                ContentKey = descriptor.Key,
                EnvelopeFrom = envelope.Sender,
                InternetMessageId = mimeMessage.MessageId,
                InternetMessageIdHash = MessageIdLookup.Hash(mimeMessage.MessageId),
                SourceType = envelope.SourceType ?? (envelope.RemoteAddress == "local" ? "system" : "smtp"),
                AuthenticatedAccountId = envelope.AuthenticatedAccountId,
                AuthenticatedAccountName = envelope.AuthenticatedAccountId is null ? null : envelope.AuthenticatedUser,
                SourceIp = IPAddress.TryParse(envelope.RemoteAddress, out _) ? envelope.RemoteAddress : null,
                Size = descriptor.Size,
                ReceivedAt = receivedAt
            };
            db.Messages.Add(storedMessage);
            PerformanceTelemetry.Resource(messageId: storedMessage.Id);
            foreach (var address in envelope.Recipients.Distinct(StringComparer.OrdinalIgnoreCase))
                db.MessageRecipients.Add(new StoredMessageRecipient { Message = storedMessage, Address = address });

            var decodedEnvelopeSender = await srs.TryDecodeAsync(db, envelope.Sender, cancellationToken);
            var forwardingTraces = await srs.ReadForwardingTracesAsync(
                db,
                mimeMessage.MessageId,
                mimeMessage.Headers
                    .Where(header => header.Field.Equals(SrsService.ForwardingTraceHeader, StringComparison.OrdinalIgnoreCase))
                    .Select(header => header.Value),
                cancellationToken);
            var forwarding = new ForwardContext(
                decodedEnvelopeSender?.OriginalSender ?? envelope.Sender,
                Math.Max(
                    decodedEnvelopeSender?.ForwardDepth ?? 0,
                    forwardingTraces.Select(trace => trace.ForwardDepth).DefaultIfEmpty(0).Max()),
                forwardingTraces.Select(trace => trace.RuleId).ToHashSet(),
                new HashSet<Guid>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var counters = new DeliveryCounters();
            // 收件人先展开成账号或外部地址，再按账号/地址分组，避免同一封信经多个别名重复投递。
            var expandedRecipients = await ExpandRecipientsAsync(db, envelope.Recipients, cancellationToken);
            foreach (var group in expandedRecipients.GroupBy(
                         x => x.Account is null ? $"external:{x.Address}" : $"account:{x.Account.Id}",
                         StringComparer.OrdinalIgnoreCase))
            {
                var recipient = group.First();
                if (recipient.Account is { } account)
                {
                    if (!forwarding.DeliveredAccounts.Add(account.Id)) continue;
                    await DeliverLocallyAsync(
                        db,
                        storedMessage,
                        descriptor,
                        mimeMessage,
                        envelope,
                        account,
                        group.Select(x => x.OriginalAddress).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        receivedAt,
                        forwarding,
                        counters,
                        cancellationToken);
                    continue;
                }

                if (envelope.AuthenticatedUser is null && !recipient.AuthorizedRelay)
                {
                    // 未认证入站只能给本地账号投递；别名转发产生的外部目标必须显式授权。
                    logger.LogInformation(
                        "Inbound message {MessageId} rejected for {Recipient}: unauthenticated relay is forbidden.",
                        storedMessage.Id,
                        recipient.Address);
                    throw new InvalidOperationException($"Unauthenticated relay to {recipient.Address} is forbidden.");
                }

                var route = recipient.RelayDomainId is { } relayDomainId
                    ? await ResolveOutboundRouteAsync(db, relayDomainId, cancellationToken)
                    : await ResolveOutboundRouteAsync(db, envelope.Sender, cancellationToken);
                db.DeliveryJobs.Add(new DeliveryJob
                {
                    Message = storedMessage,
                    Sender = envelope.Sender,
                    Recipient = recipient.Address,
                    RouteDomainId = route.DomainId
                });
                counters.Queued++;
            }

            if (envelope.AuthenticatedAccountId is { } authenticatedAccountId)
            {
                // 已认证发信会尝试排队保存 Sent 副本，用于弥补部分客户端不主动 APPEND 到 Sent 的情况。
                var outgoingMatch = await QueueSentCopyAsync(
                    db,
                    authenticatedAccountId,
                    storedMessage,
                    mimeMessage,
                    envelope,
                    descriptor.Size,
                    receivedAt,
                    cancellationToken);
                if (outgoingMatch is not null)
                {
                    // 发信规则的转发动作基于已认证账号的域，转发副本单独排队/投递，原收件人不受影响。
                    await ApplyOutgoingForwardingAsync(
                        db,
                        storedMessage,
                        mimeMessage,
                        envelope,
                        descriptor.Size,
                        receivedAt,
                        outgoingMatch,
                        forwarding,
                        counters,
                        cancellationToken);
                }
            }

            if (counters.Local == 0 && counters.Queued == 0)
                throw new InvalidOperationException("Message has no valid recipients.");

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Inbound message {MessageId} accepted: localDeliveries={LocalDeliveries}, queuedDeliveries={QueuedDeliveries}, contentKey={ContentKey}.",
                storedMessage.Id,
                counters.Local,
                counters.Queued,
                descriptor.Key);
            return new InboundAcceptance(storedMessage.Id, descriptor.Key, counters.Local, counters.Queued);
        }
        catch (Exception failure)
        {
            performance?.Fail(failure);
            // 只要事务失败，就尝试删除还没有任何数据库引用的内容文件，避免孤儿附件堆积。
            try
            {
                await contentStore.DeleteIfUnreferencedAsync(descriptor.Key, cancellationToken);
            }
            catch (Exception cleanupFailure) when (failure is MailboxQuotaExceededException or DomainQuotaExceededException
                && cleanupFailure is not OperationCanceledException)
            {
                logger.LogWarning(cleanupFailure, "Unable to clean up rejected quota message content {ContentKey}.", descriptor.Key);
            }
            throw;
        }
    }

    private async Task<MimeMessage> LoadMimeMessageWithFallbackAsync(
        string contentKey,
        CancellationToken cancellationToken)
    {
        using var performance = PerformanceTelemetry.Stage("mime.parse");
        await using var parseStream = await contentStore.OpenReadAsync(contentKey, cancellationToken);
        try
        {
            return await MimeMessage.LoadAsync(parseStream, cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            // 某些客户端会发送非标准 MIME。降级为纯文本可保留原始内容并避免整封邮件丢失。
            logger.LogInformation(
                exception,
                "Inbound message content {ContentKey} could not be parsed as MIME; using body-only fallback.",
                contentKey);
            parseStream.Position = 0;
            using var reader = new StreamReader(parseStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken);
            return new MimeMessage { Body = new TextPart("plain") { Text = text } };
        }
    }

    private async Task AuditMimeInspectionAsync(
        MailDbContext db,
        InboundEnvelope envelope,
        string contentKey,
        CancellationToken cancellationToken)
    {
        await using var stream = await contentStore.OpenReadAsync(contentKey, cancellationToken);
        var inspection = mimeInspection.Inspect(stream);
        if (inspection.RiskFlags.Count == 0
            && inspection.Authentication.AuthenticationResults.Count == 0
            && inspection.Authentication.ArcResults.Count == 0
            && inspection.Authentication.DkimSignatures.Count == 0)
        {
            return;
        }

        db.AuditRecords.Add(new AuditRecord
        {
            Actor = envelope.AuthenticatedUser ?? "anonymous",
            Action = "smtp.inbound.mime-inspection",
            Resource = $"content:{contentKey}",
            RemoteAddress = envelope.RemoteAddress,
            Detail = $"risks={string.Join(',', inspection.RiskFlags)}; authenticationResults={inspection.Authentication.AuthenticationResults.Count}; arc={inspection.Authentication.ArcResults.Count}; dkim={inspection.Authentication.DkimSignatures.Count}; attachments={inspection.Attachments.Count}"
        });
    }

    private static async Task AuditDuplicateInboundAsync(
        MailDbContext db,
        InboundEnvelope envelope,
        ContentDescriptor descriptor,
        MimeMessage mimeMessage,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        var since = receivedAt.AddMinutes(-15);
        var internetMessageId = mimeMessage.MessageId;
        var duplicate = await db.Messages.AsNoTracking()
            .Where(x => x.ReceivedAt >= since
                        && x.ContentKey == descriptor.Key
                        && x.EnvelopeFrom == envelope.Sender
                        && (internetMessageId == null || x.InternetMessageId == internetMessageId))
            .AnyAsync(cancellationToken);
        if (!duplicate) return;
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = envelope.AuthenticatedUser ?? "anonymous",
            Action = "smtp.inbound.duplicate-suspected",
            Resource = $"content:{descriptor.Key}",
            RemoteAddress = envelope.RemoteAddress,
            Detail = $"sender={envelope.Sender}; recipients={string.Join(',', envelope.Recipients)}; messageId={internetMessageId ?? string.Empty}"
        });
    }

    private static async Task AuditLocalDomainSpoofAsync(
        MailDbContext db,
        InboundEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (envelope.AuthenticatedUser is not null
            || !AddressParser.TrySplit(envelope.Sender, out _, out var senderDomain)
            || !await db.Domains.AsNoTracking().AnyAsync(x => x.Name == senderDomain && x.IsActive, cancellationToken))
            return;
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = "anonymous",
            Action = "smtp.inbound.local-domain-spoof",
            Resource = $"domain:{senderDomain}",
            RemoteAddress = envelope.RemoteAddress,
            Detail = $"sender={envelope.Sender}; recipients={string.Join(',', envelope.Recipients)}"
        });
    }

    private static void AuditDuplicateSingletonHeaders(
        MailDbContext db,
        InboundEnvelope envelope,
        ContentDescriptor descriptor,
        MimeMessage mimeMessage)
    {
        var duplicated = mimeMessage.Headers
            .Where(header => header.Field.Equals("Subject", StringComparison.OrdinalIgnoreCase)
                             || header.Field.Equals("From", StringComparison.OrdinalIgnoreCase)
                             || header.Field.Equals("Message-ID", StringComparison.OrdinalIgnoreCase))
            .GroupBy(header => header.Field, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}:{group.Count()}")
            .ToArray();
        if (duplicated.Length == 0) return;
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = envelope.AuthenticatedUser ?? "anonymous",
            Action = "smtp.inbound.duplicate-header",
            Resource = $"content:{descriptor.Key}",
            RemoteAddress = envelope.RemoteAddress,
            Detail = string.Join(';', duplicated)
        });
    }

    private async Task DeliverLocallyAsync(
        MailDbContext db,
        StoredMessage storedMessage,
        ContentDescriptor descriptor,
        MimeMessage mimeMessage,
        InboundEnvelope envelope,
        MailAccount account,
        IReadOnlyList<string> originalRecipients,
        DateTimeOffset receivedAt,
        ForwardContext forwarding,
        DeliveryCounters counters,
        CancellationToken cancellationToken)
    {
        using var performance = PerformanceTelemetry.Stage("mailbox.deliver-local");
        if (account.UsedBytes + descriptor.Size > account.QuotaBytes)
            throw new MailboxQuotaExceededException($"{account.LocalPart}@{account.Domain.Name}");
        if (account.Domain.UsedBytes + descriptor.Size > account.Domain.QuotaBytes)
            throw new DomainQuotaExceededException(account.Domain.Name, $"{account.LocalPart}@{account.Domain.Name}");

        var facts = MailRuleService.BuildFacts(
            mimeMessage,
            envelope.Sender,
            originalRecipients,
            descriptor.Size,
            receivedAt);
        var match = await rules.MatchAsync(account.DomainId, MailRuleDirection.Incoming, facts, cancellationToken);
        var requestedMailbox = match?.Actions
            .FirstOrDefault(x => x.Kind == MailRuleActionKind.MoveToFolder)?.Values.FirstOrDefault()
            ?? "INBOX";
        var mailbox = await db.Mailboxes.SingleOrDefaultAsync(
            x => x.AccountId == account.Id && x.Name == requestedMailbox && x.PendingDeletionJobId == null,
            cancellationToken);
        if (mailbox is null && !requestedMailbox.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Incoming rule target mailbox {TargetMailbox} does not exist for account {AccountId}; falling back to INBOX.",
                requestedMailbox,
                account.Id);
            mailbox = await db.Mailboxes.SingleOrDefaultAsync(
                x => x.AccountId == account.Id && x.Name == "INBOX" && x.PendingDeletionJobId == null,
                cancellationToken);
        }

        if (mailbox is null)
        {
            mailbox = new Mailbox { Account = account, Name = "INBOX" };
            db.Mailboxes.Add(mailbox);
        }

        await MailboxService.EnsureUidNextAtLeastAsync(db, mailbox, cancellationToken);
        var item = new MailboxMessage
        {
            Mailbox = mailbox,
            Message = storedMessage,
            Uid = mailbox.UidNext++,
            ModSeq = ++mailbox.HighestModSeq,
            InternalDate = receivedAt
        };
        db.MailboxMessages.Add(item);
        account.UsedBytes += descriptor.Size;
        account.Domain.UsedBytes += descriptor.Size;
        db.QuotaLedger.Add(new QuotaLedgerEntry
        {
            IdempotencyKey = $"mailbox-message:add:{item.Id:N}",
            AccountId = account.Id,
            DomainId = account.DomainId,
            MailboxMessageId = item.Id,
            Operation = QuotaLedgerOperation.Add,
            Bytes = descriptor.Size
        });
        counters.Local++;
        logger.LogInformation(
            "Inbound message {MessageId} delivered locally to account {AccountId}: originalRecipients={OriginalRecipients}, mailbox={Mailbox}, uid={Uid}.",
            storedMessage.Id,
            account.Id,
            string.Join(',', originalRecipients),
            mailbox.Name,
            item.Uid);

        if (match is null || forwarding.Depth >= Math.Clamp(_forwardingOptions.MaxHops, 1, 20)
            || !forwarding.ExecutedRules.Add(match.RuleId))
            return;

        var targets = match.Actions.Where(x => x.Kind == MailRuleActionKind.ForwardTo)
            .SelectMany(x => x.Values)
            .Select(AddressParser.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(_forwardingOptions.MaxTargetsPerRule, 1, 100));
        foreach (var target in targets)
        {
            if (originalRecipients.Contains(target, StringComparer.OrdinalIgnoreCase)
                || !forwarding.ForwardedTargets.Add(target))
                continue;
            try
            {
                var targetAccount = await ResolveLocalAccountAsync(db, target, cancellationToken);
                var child = forwarding with { Depth = forwarding.Depth + 1 };
                if (targetAccount is not null)
                {
                    if (!forwarding.DeliveredAccounts.Add(targetAccount.Id)) continue;
                    await DeliverLocallyAsync(
                        db,
                        storedMessage,
                        descriptor,
                        mimeMessage,
                        envelope,
                        targetAccount,
                        [target],
                        receivedAt,
                        child,
                        counters,
                        cancellationToken);
                    continue;
                }

                var sender = string.IsNullOrWhiteSpace(forwarding.OriginalSender)
                    ? string.Empty
                    : srs.CreateAddress(db, account.Domain, forwarding.OriginalSender, child.Depth, receivedAt);
                db.DeliveryJobs.Add(new DeliveryJob
                {
                    Message = storedMessage,
                    Sender = sender,
                    Recipient = target,
                    RouteDomainId = account.DomainId,
                    OriginRuleId = match.RuleId,
                    ForwardDepth = child.Depth
                });
                counters.Queued++;
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException or MailboxQuotaExceededException or DomainQuotaExceededException)
            {
                logger.LogWarning(
                    exception,
                    "Incoming rule {RuleId} could not forward message {MessageId} to {Target}; local delivery was retained.",
                    match.RuleId,
                    storedMessage.Id,
                    target);
            }
        }
    }

    /// <summary>
    /// 发信规则命中转发动作后，把同一封信额外投递给目标地址。
    /// 本地目标写入邮箱，外部目标进入投递队列；转发深度复用收信链路的 Hop 限制。
    /// </summary>
    private async Task ApplyOutgoingForwardingAsync(
        MailDbContext db,
        StoredMessage storedMessage,
        MimeMessage mimeMessage,
        InboundEnvelope envelope,
        long size,
        DateTimeOffset sentAt,
        MailRuleMatch match,
        ForwardContext forwarding,
        DeliveryCounters counters,
        CancellationToken cancellationToken)
    {
        if (forwarding.Depth >= Math.Clamp(_forwardingOptions.MaxHops, 1, 20)
            || !forwarding.ExecutedRules.Add(match.RuleId))
            return;
        var targets = match.Actions
            .Where(x => x.Kind == MailRuleActionKind.ForwardTo)
            .SelectMany(x => x.Values)
            .Select(AddressParser.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(_forwardingOptions.MaxTargetsPerRule, 1, 100));
        foreach (var target in targets)
        {
            if (envelope.Recipients.Contains(target, StringComparer.OrdinalIgnoreCase)
                || !forwarding.ForwardedTargets.Add(target))
                continue;
            try
            {
                var targetAccount = await ResolveLocalAccountAsync(db, target, cancellationToken);
                if (targetAccount is not null)
                {
                    if (!forwarding.DeliveredAccounts.Add(targetAccount.Id)) continue;
                    await DeliverLocallyAsync(
                        db,
                        storedMessage,
                        new ContentDescriptor(storedMessage.ContentKey, size),
                        mimeMessage,
                        envelope,
                        targetAccount,
                        [target],
                        sentAt,
                        forwarding with { Depth = forwarding.Depth + 1 },
                        counters,
                        cancellationToken);
                    continue;
                }

                db.DeliveryJobs.Add(new DeliveryJob
                {
                    Message = storedMessage,
                    Sender = envelope.Sender,
                    Recipient = target,
                    RouteDomainId = await ResolveOutboundRouteDomainIdAsync(db, envelope.Sender, cancellationToken),
                    OriginRuleId = match.RuleId,
                    ForwardDepth = forwarding.Depth + 1
                });
                counters.Queued++;
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException or MailboxQuotaExceededException or DomainQuotaExceededException)
            {
                logger.LogWarning(
                    exception,
                    "Outgoing rule {RuleId} could not forward message {MessageId} to {Target}; the original submission was retained.",
                    match.RuleId,
                    storedMessage.Id,
                    target);
            }
        }
    }

    private static async Task<Guid?> ResolveOutboundRouteDomainIdAsync(
        MailDbContext db,
        string sender,
        CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(sender, out _, out var domain)) return null;
        return await db.Domains.Where(x => x.Name == domain)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<MailRuleMatch?> QueueSentCopyAsync(
        MailDbContext db,
        Guid authenticatedAccountId,
        StoredMessage storedMessage,
        MimeMessage mimeMessage,
        InboundEnvelope envelope,
        long size,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        var account = await db.Accounts.Include(x => x.Domain)
            .SingleOrDefaultAsync(
                x => x.Id == authenticatedAccountId && x.IsEnabled && x.Domain.IsActive,
                cancellationToken);
        if (account is null) return null;
        var facts = MailRuleService.BuildFacts(mimeMessage, envelope.Sender, envelope.Recipients, size, sentAt);
        var match = await rules.MatchAsync(account.DomainId, MailRuleDirection.Outgoing, facts, cancellationToken);
        var targetMailboxName = match?.TargetMailboxName ?? "Sent";
        var targetExists = await db.Mailboxes.AnyAsync(
            x => x.AccountId == account.Id && x.Name == targetMailboxName && x.PendingDeletionJobId == null,
            cancellationToken);
        if (!targetExists && !targetMailboxName.Equals("Sent", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Outgoing rule target mailbox {TargetMailbox} does not exist for account {AccountId}; falling back to Sent.",
                targetMailboxName,
                account.Id);
            targetMailboxName = "Sent";
        }

        if (!await db.Mailboxes.AnyAsync(x => x.AccountId == account.Id && x.Name == "Sent", cancellationToken))
            db.Mailboxes.Add(new Mailbox { Account = account, Name = "Sent" });
        sentCopies.Queue(db, account, storedMessage, mimeMessage, targetMailboxName, match?.RuleId);
        return match;
    }

    private async Task<IReadOnlyList<ExpandedRecipient>> ExpandRecipientsAsync(
        MailDbContext db,
        IReadOnlyList<string> recipients,
        CancellationToken cancellationToken)
    {
        using var performance = PerformanceTelemetry.Stage("routing.expand");
        var result = new List<ExpandedRecipient>();
        foreach (var rawRecipient in recipients)
        {
            var recipient = AddressParser.Normalize(rawRecipient);
            var srsReturn = await srs.TryDecodeAsync(db, recipient, cancellationToken);
            if (srsReturn is not null) recipient = srsReturn.OriginalSender;
            if (!AddressParser.TrySplit(recipient, out var localPart, out var domain)) continue;
            var account = await ResolveLocalAccountAsync(db, recipient, cancellationToken);

            result.Add(account is null
                ? new ExpandedRecipient(rawRecipient, recipient, null, srsReturn is not null, srsReturn?.DomainId)
                : new ExpandedRecipient(rawRecipient, $"{account.LocalPart}@{account.Domain.Name}", account, srsReturn is not null, srsReturn?.DomainId));
        }

        return result;
    }

    private static async Task<MailAccount?> ResolveLocalAccountAsync(
        MailDbContext db,
        string address,
        CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(address, out var localPart, out var domain)) return null;
        var account = await db.Accounts.Include(x => x.Domain)
            .SingleOrDefaultAsync(
                x => x.LocalPart == localPart && x.Domain.Name == domain && x.IsEnabled && x.Domain.IsActive,
                cancellationToken);
        if (account is not null) return account;
        var alias = await db.Aliases.Include(x => x.TargetAccount).ThenInclude(x => x!.Domain)
            .SingleOrDefaultAsync(
                x => x.SourceLocalPart == localPart
                     && x.Domain.Name == domain
                     && x.Domain.IsActive
                     && x.IsEnabled
                     && x.Mode != MailAliasMode.Disabled
                     && x.TargetAccountId != null
                     && x.TargetAccount!.IsEnabled
                     && x.TargetAccount.Domain.IsActive,
                cancellationToken);
        return alias?.TargetAccount;
    }

    private sealed record ExpandedRecipient(
        string OriginalAddress,
        string Address,
        MailAccount? Account,
        bool AuthorizedRelay,
        Guid? RelayDomainId);

    private sealed record ForwardContext(
        string OriginalSender,
        int Depth,
        HashSet<Guid> ExecutedRules,
        HashSet<Guid> DeliveredAccounts,
        HashSet<string> ForwardedTargets);

    private sealed class DeliveryCounters
    {
        public int Local { get; set; }
        public int Queued { get; set; }
    }

    private static async Task<OutboundRoute> ResolveOutboundRouteAsync(
        MailDbContext db,
        string sender,
        CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(sender, out _, out var domain)) return new OutboundRoute(null);
        return await db.Domains.Where(x => x.Name == domain)
            .Select(x => new OutboundRoute(x.Id))
            .SingleOrDefaultAsync(cancellationToken) ?? new OutboundRoute(null);
    }

    private static async Task<OutboundRoute> ResolveOutboundRouteAsync(
        MailDbContext db,
        Guid domainId,
        CancellationToken cancellationToken) =>
        await db.Domains.Where(x => x.Id == domainId)
            .Select(x => new OutboundRoute(x.Id))
            .SingleOrDefaultAsync(cancellationToken) ?? new OutboundRoute(null);

    private sealed record OutboundRoute(Guid? DomainId);
}
