namespace MailServer.Domain;

public enum OutboundMode
{
    Relay = 0,
    DirectMx = 1
}

public enum DeliveryStatus
{
    Pending = 0,
    Processing = 1,
    Delivered = 2,
    Deferred = 3,
    Failed = 4
}

public sealed class MailDomain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public OutboundMode OutboundMode { get; set; } = OutboundMode.Relay;
    public string? DkimSelector { get; set; }
    public string? DkimPrivateKeyPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MailAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DomainId { get; set; }
    public MailDomain Domain { get; set; } = null!;
    public required string LocalPart { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsEnabled { get; set; } = true;
    public long QuotaBytes { get; set; } = 1_073_741_824;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MailAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DomainId { get; set; }
    public MailDomain Domain { get; set; } = null!;
    public required string SourceLocalPart { get; set; }
    public required string DestinationAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Mailbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public MailAccount Account { get; set; } = null!;
    public required string Name { get; set; }
    public long UidValidity { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long UidNext { get; set; } = 1;
    public long HighestModSeq { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StoredMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string ContentKey { get; set; }
    public required string EnvelopeFrom { get; set; }
    public string? InternetMessageId { get; set; }
    public long Size { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MailboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MailboxId { get; set; }
    public Mailbox Mailbox { get; set; } = null!;
    public Guid MessageId { get; set; }
    public StoredMessage Message { get; set; } = null!;
    public long Uid { get; set; }
    public string Flags { get; set; } = string.Empty;
    public long ModSeq { get; set; } = 1;
    public DateTimeOffset InternalDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DeliveryJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public StoredMessage Message { get; set; } = null!;
    public required string Sender { get; set; }
    public required string Recipient { get; set; }
    public OutboundMode RouteMode { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DeliveryAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeliveryJobId { get; set; }
    public DeliveryJob DeliveryJob { get; set; } = null!;
    public int AttemptNumber { get; set; }
    public bool Succeeded { get; set; }
    public bool PermanentFailure { get; set; }
    public string? RemoteHost { get; set; }
    public string? Response { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string UserName { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ApiKeyRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Prefix { get; set; }
    public required string SecretHash { get; set; }
    public required string Scopes { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string Resource { get; set; }
    public string? Detail { get; set; }
    public string? RemoteAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
