using System.Text;

namespace Relay.Application;

public static class MessageValidator
{
    public static Dictionary<string, string[]> Validate(SendMessageRequest request, MessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var to = request.To ?? [];
        var cc = request.Cc ?? [];
        var bcc = request.Bcc ?? [];
        if (to.Count == 0)
        {
            errors["to"] = ["At least one recipient is required."];
        }

        CheckAddresses("to", to, errors);
        CheckAddresses("cc", cc, errors);
        CheckAddresses("bcc", bcc, errors);
        if (to.Count + cc.Count + bcc.Count > options.MaxRecipients)
        {
            errors["recipients"] = [$"At most {options.MaxRecipients} recipients across to, cc and bcc."];
        }

        CheckContent(request.From, request.Subject, request.Text, request.Html, options, errors);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(SendBulkRequest request, MessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var recipients = request.Recipients ?? [];
        if (recipients.Count == 0)
        {
            errors["recipients"] = ["At least one recipient is required."];
        }
        else if (recipients.Count > options.MaxBulkRecipients)
        {
            errors["recipients"] = [$"At most {options.MaxBulkRecipients} recipients per bulk request."];
        }
        else
        {
            CheckAddresses("recipients", recipients, errors);
        }

        CheckContent(request.From, request.Subject, request.Text, request.Html, options, errors);
        return errors;
    }

    private static void CheckContent(
        string? from, string? subject, string? text, string? html, MessagingOptions options, Dictionary<string, string[]> errors)
    {
        if (from is not null && !EmailAddress.IsValid(from))
        {
            errors["from"] = ["Must be a single valid email address with no display name."];
        }

        if (string.IsNullOrWhiteSpace(subject))
        {
            errors["subject"] = ["Subject is required."];
        }
        else if (subject.Any(char.IsControl))
        {
            errors["subject"] = ["Subject must not contain control characters such as CR or LF."];
        }
        else if (subject.Length > options.MaxSubjectLength)
        {
            errors["subject"] = [$"Subject must be at most {options.MaxSubjectLength} characters."];
        }

        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(html))
        {
            errors["body"] = ["Provide text, html, or both."];
        }
        else if (Encoding.UTF8.GetByteCount(text ?? string.Empty) + Encoding.UTF8.GetByteCount(html ?? string.Empty) > options.MaxBodyBytes)
        {
            errors["body"] = [$"Text and html together must be at most {options.MaxBodyBytes} bytes."];
        }
    }

    private static void CheckAddresses(string field, IReadOnlyList<string> addresses, Dictionary<string, string[]> errors)
    {
        var bad = addresses
            .Select((address, index) => (address, index))
            .Where(x => !EmailAddress.IsValid(x.address))
            .Select(x => $"Entry {x.index} is not a valid email address.")
            .ToArray();
        if (bad.Length > 0)
        {
            errors[field] = bad;
        }
    }
}
