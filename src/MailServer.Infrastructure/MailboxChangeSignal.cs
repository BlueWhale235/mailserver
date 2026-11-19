namespace MailServer.Infrastructure;

/// <summary>Mailbox commit hint, also relayed to other local processes.</summary>
public sealed class MailboxChangeSignal
{
    public event Action<long>? Changed;
    internal event Action<long>? Published;
    public void Pulse(long mailboxId) { PulseLocal(mailboxId); Published?.Invoke(mailboxId); }
    internal void PulseLocal(long mailboxId) => Changed?.Invoke(mailboxId);
}
