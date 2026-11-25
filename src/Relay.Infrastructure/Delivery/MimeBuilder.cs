using MimeKit;
using Relay.Application;

namespace Relay.Infrastructure.Delivery;

internal static class MimeBuilder
{
    public static MimeMessage Build(OutgoingEmail email, DateTimeOffset date, bool includeBcc)
    {
        var message = new MimeMessage
        {
            MessageId = email.InternetMessageId.Trim('<', '>'),
            Date = date,
            Subject = email.Subject,
        };
        message.From.Add(MailboxAddress.Parse(email.From));
        message.To.AddRange(email.To.Select(MailboxAddress.Parse));
        message.Cc.AddRange(email.Cc.Select(MailboxAddress.Parse));
        if (includeBcc)
        {
            message.Bcc.AddRange(email.Bcc.Select(MailboxAddress.Parse));
        }

        message.Body = new BodyBuilder { TextBody = email.TextBody, HtmlBody = email.HtmlBody }.ToMessageBody();
        return message;
    }
}
