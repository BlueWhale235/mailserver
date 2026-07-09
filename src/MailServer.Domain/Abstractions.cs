namespace MailServer.Domain;

public sealed record ContentDescriptor(string Key, long Size);

public sealed record InboundEnvelope(
    string Sender,
    IReadOnlyList<string> Recipients,
    string? AuthenticatedUser,
    string RemoteAddress,
    string HeloName,
    Guid? AuthenticatedAccountId = null);

public sealed record InboundAcceptance(Guid MessageId, string ContentKey, int LocalDeliveries, int QueuedDeliveries);

public sealed record DeliveryResult(bool Succeeded, bool PermanentFailure, string Response, string? RemoteHost = null)
{
    public static DeliveryResult Success(string response, string? host = null) => new(true, false, response, host);
    public static DeliveryResult Temporary(string response, string? host = null) => new(false, false, response, host);
    public static DeliveryResult Permanent(string response, string? host = null) => new(false, true, response, host);
}

public sealed record MailboxSummary(Guid Id, string Name, long Exists, long Unseen, long UidValidity, long UidNext, long HighestModSeq);

public sealed record MailboxItem(
    Guid Id,
    long Sequence,
    long Uid,
    string Flags,
    long ModSeq,
    DateTimeOffset InternalDate,
    long Size,
    string ContentKey);

public sealed record ExpungedMailboxItem(long Sequence, long Uid);

public sealed record CopyMoveResult(
    long SourceUidValidity,
    long TargetUidValidity,
    IReadOnlyList<long> SourceUids,
    IReadOnlyList<long> TargetUids,
    IReadOnlyList<ExpungedMailboxItem> ExpungedItems);

public interface IMessageContentStore
{
    Task<ContentDescriptor> SaveAsync(Stream content, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);
    Task DeleteIfUnreferencedAsync(string key, CancellationToken cancellationToken);
}

public interface ICredentialService
{
    string HashPassword(string password);
    bool VerifyPassword(string hash, string password, out string? upgradedHash);
    string HashApiSecret(string secret);
    bool VerifyApiSecret(string hash, string secret);
}

public interface IAccountAuthenticator
{
    Task<Guid?> AuthenticateMailboxAsync(string address, string password, CancellationToken cancellationToken);
    Task<Guid?> ResolveMailboxIdentityAsync(string address, CancellationToken cancellationToken);
}

public interface ISmtpSenderAuthorizer
{
    Task<bool> IsAuthorizedSenderAsync(Guid accountId, string sender, CancellationToken cancellationToken);
}

public interface IInboundMessageService
{
    Task<bool> IsLocalRecipientAsync(string address, CancellationToken cancellationToken);
    Task<InboundAcceptance> AcceptAsync(InboundEnvelope envelope, Stream content, CancellationToken cancellationToken);
}

public interface IDeliveryQueue
{
    Task<IReadOnlyList<DeliveryJob>> LeaseAsync(string owner, int count, TimeSpan duration, CancellationToken cancellationToken);
    Task CompleteAsync(Guid jobId, DeliveryResult result, CancellationToken cancellationToken);
    Task RetryNowAsync(Guid jobId, CancellationToken cancellationToken);
    Task DeleteAsync(Guid jobId, CancellationToken cancellationToken);
}

public interface IOutboundTransport
{
    OutboundMode Mode { get; }
    Task<DeliveryResult> DeliverAsync(DeliveryJob job, Stream content, CancellationToken cancellationToken);
}

public interface IMailboxService
{
    Task<IReadOnlyList<MailboxSummary>> ListAsync(Guid accountId, CancellationToken cancellationToken);
    Task EnsureDefaultFoldersAsync(Guid accountId, CancellationToken cancellationToken);
    Task<MailboxSummary?> SelectAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken);
    Task<MailboxSummary> CreateAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken);
    Task DeleteAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken);
    Task<IReadOnlyList<MailboxItem>> FetchAsync(Guid mailboxId, string sequenceSet, bool useUid, CancellationToken cancellationToken);
    Task<IReadOnlyList<long>> SearchAsync(Guid mailboxId, string criteria, bool useUid, CancellationToken cancellationToken);
    Task StoreFlagsAsync(Guid mailboxId, string sequenceSet, bool useUid, string operation, IReadOnlyCollection<string> flags, CancellationToken cancellationToken);
    Task MarkSeenAsync(Guid mailboxId, IReadOnlyCollection<Guid> mailboxMessageIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<ExpungedMailboxItem>> ExpungeAsync(Guid mailboxId, CancellationToken cancellationToken);
    Task<CopyMoveResult> CopyAsync(Guid mailboxId, string sequenceSet, bool useUid, string destination, bool move, CancellationToken cancellationToken);
    Task<MailboxItem> AppendAsync(Guid accountId, string mailboxName, Stream content, IReadOnlyCollection<string> flags, DateTimeOffset? internalDate, CancellationToken cancellationToken);
    Task<Stream> OpenMessageAsync(Guid mailboxMessageId, CancellationToken cancellationToken);
}
