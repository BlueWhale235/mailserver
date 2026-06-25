using System.Data;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MailServer.Infrastructure;

public sealed class InboundMessageService(
    IDbContextFactory<MailDbContext> dbFactory,
    IMessageContentStore contentStore) : IInboundMessageService
{
    public async Task<bool> IsLocalRecipientAsync(string address, CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(address, out var localPart, out var domainName))
        {
            return false;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Accounts.AnyAsync(
                   x => x.LocalPart == localPart && x.Domain.Name == domainName && x.IsEnabled && x.Domain.IsActive,
                   cancellationToken)
               || await db.Aliases.AnyAsync(
                   x => x.SourceLocalPart == localPart && x.Domain.Name == domainName && x.Domain.IsActive,
                   cancellationToken);
    }

    public async Task<InboundAcceptance> AcceptAsync(
        InboundEnvelope envelope,
        Stream content,
        CancellationToken cancellationToken)
    {
        var descriptor = await contentStore.SaveAsync(content, cancellationToken);
        try
        {
            string? internetMessageId = null;
            await using (var parseStream = await contentStore.OpenReadAsync(descriptor.Key, cancellationToken))
            {
                var parsed = await MimeMessage.LoadAsync(parseStream, cancellationToken);
                internetMessageId = parsed.MessageId;
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            var storedMessage = new StoredMessage
            {
                ContentKey = descriptor.Key,
                EnvelopeFrom = envelope.Sender,
                InternetMessageId = internetMessageId,
                Size = descriptor.Size
            };
            db.Messages.Add(storedMessage);

            var expandedRecipients = await ExpandRecipientsAsync(db, envelope.Recipients, cancellationToken);
            var localDeliveries = 0;
            var queuedDeliveries = 0;

            foreach (var recipient in expandedRecipients
                         .GroupBy(x => x.Address, StringComparer.OrdinalIgnoreCase)
                         .Select(group => new ExpandedRecipient(
                             group.Key,
                             group.Any(x => x.AuthorizedByLocalAlias))))
            {
                if (await FindLocalAccountAsync(db, recipient.Address, cancellationToken) is { } account)
                {
                    var currentUsage = await db.MailboxMessages
                        .Where(x => x.Mailbox.AccountId == account.Id)
                        .SumAsync(x => (long?)x.Message.Size, cancellationToken) ?? 0;
                    if (currentUsage + descriptor.Size > account.QuotaBytes)
                    {
                        throw new InvalidOperationException($"Mailbox quota exceeded for {recipient.Address}.");
                    }

                    var inbox = await db.Mailboxes.SingleOrDefaultAsync(
                        x => x.AccountId == account.Id && x.Name == "INBOX",
                        cancellationToken);
                    if (inbox is null)
                    {
                        inbox = new Mailbox { AccountId = account.Id, Name = "INBOX" };
                        db.Mailboxes.Add(inbox);
                    }

                    var uid = inbox.UidNext++;
                    var modSeq = ++inbox.HighestModSeq;
                    db.MailboxMessages.Add(new MailboxMessage
                    {
                        Mailbox = inbox,
                        Message = storedMessage,
                        Uid = uid,
                        ModSeq = modSeq
                    });
                    localDeliveries++;
                    continue;
                }

                if (envelope.AuthenticatedUser is null && !recipient.AuthorizedByLocalAlias)
                {
                    throw new InvalidOperationException($"Unauthenticated relay to {recipient.Address} is forbidden.");
                }

                db.DeliveryJobs.Add(new DeliveryJob
                {
                    Message = storedMessage,
                    Sender = envelope.Sender,
                    Recipient = recipient.Address,
                    RouteMode = await ResolveOutboundModeAsync(db, envelope.Sender, cancellationToken)
                });
                queuedDeliveries++;
            }

            if (localDeliveries == 0 && queuedDeliveries == 0)
            {
                throw new InvalidOperationException("Message has no valid recipients.");
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new InboundAcceptance(storedMessage.Id, descriptor.Key, localDeliveries, queuedDeliveries);
        }
        catch
        {
            await contentStore.DeleteIfUnreferencedAsync(descriptor.Key, cancellationToken);
            throw;
        }
    }

    private static async Task<IReadOnlyList<ExpandedRecipient>> ExpandRecipientsAsync(
        MailDbContext db,
        IReadOnlyList<string> recipients,
        CancellationToken cancellationToken)
    {
        var result = new List<ExpandedRecipient>();
        foreach (var rawRecipient in recipients)
        {
            var recipient = AddressParser.Normalize(rawRecipient);
            if (!AddressParser.TrySplit(recipient, out var localPart, out var domain))
            {
                continue;
            }

            var alias = await db.Aliases
                .Include(x => x.Domain)
                .SingleOrDefaultAsync(
                    x => x.SourceLocalPart == localPart && x.Domain.Name == domain && x.Domain.IsActive,
                    cancellationToken);
            result.Add(alias is null
                ? new ExpandedRecipient(recipient, false)
                : new ExpandedRecipient(AddressParser.Normalize(alias.DestinationAddress), true));
        }

        return result;
    }

    private sealed record ExpandedRecipient(string Address, bool AuthorizedByLocalAlias);

    private static async Task<MailAccount?> FindLocalAccountAsync(
        MailDbContext db,
        string address,
        CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(address, out var localPart, out var domain))
        {
            return null;
        }

        return await db.Accounts
            .Include(x => x.Domain)
            .SingleOrDefaultAsync(
                x => x.LocalPart == localPart && x.Domain.Name == domain && x.IsEnabled && x.Domain.IsActive,
                cancellationToken);
    }

    private static async Task<OutboundMode> ResolveOutboundModeAsync(
        MailDbContext db,
        string sender,
        CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(sender, out _, out var domain))
        {
            return OutboundMode.Relay;
        }

        return await db.Domains
            .Where(x => x.Name == domain)
            .Select(x => (OutboundMode?)x.OutboundMode)
            .SingleOrDefaultAsync(cancellationToken) ?? OutboundMode.Relay;
    }
}
