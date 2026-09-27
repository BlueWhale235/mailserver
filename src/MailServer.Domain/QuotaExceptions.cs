namespace MailServer.Domain;

public sealed class MailboxQuotaExceededException(string address) : Exception($"Mailbox quota exceeded for {address}.")
{
    public string Address { get; } = address;
}

public sealed class DomainQuotaExceededException(string domain, string address) : Exception($"Domain quota exceeded for {domain}.")
{
    public string Domain { get; } = domain;
    public string Address { get; } = address;
}
