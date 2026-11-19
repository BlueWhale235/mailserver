using MailKit.Net.Smtp;
using MimeKit;

namespace MailServer.Infrastructure;

internal sealed class RecipientSmtpClient : SmtpClient
{
    internal bool CollectRecipientFailures { get; set; }
    internal Dictionary<string, SmtpResponse> Rejected { get; } = new(StringComparer.OrdinalIgnoreCase);
    protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
    {
        if (!CollectRecipientFailures) { base.OnRecipientNotAccepted(message, mailbox, response); return; }
        Rejected[mailbox.Address] = response;
    }
    protected override void OnNoRecipientsAccepted(MimeMessage message) =>
        throw new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.TransactionFailed, "No recipients were accepted.");
}
