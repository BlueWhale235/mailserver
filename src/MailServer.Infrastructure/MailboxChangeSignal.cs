namespace MailServer.Infrastructure;

/// <summary>Local commit notification; periodic IMAP polling covers other processes.</summary>
public sealed class MailboxChangeSignal
{
    public event Action<Guid>? Changed;
    public void Pulse(Guid mailboxId) => Changed?.Invoke(mailboxId);
}
