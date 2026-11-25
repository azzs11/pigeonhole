using System.Text;

namespace Relay.Application;

public static class MessageValidator
{
    public static Dictionary<string, string[]> Validate(SendMessageRequest request, MessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.From is not null && !EmailAddress.IsValid(request.From))
        {
            errors["from"] = ["Must be a single valid email address with no display name."];
        }

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

        if (string.IsNullOrWhiteSpace(request.Subject))
        {
            errors["subject"] = ["Subject is required."];
        }
        else if (request.Subject.Any(char.IsControl))
        {
            errors["subject"] = ["Subject must not contain control characters such as CR or LF."];
        }
        else if (request.Subject.Length > options.MaxSubjectLength)
        {
            errors["subject"] = [$"Subject must be at most {options.MaxSubjectLength} characters."];
        }

        if (string.IsNullOrEmpty(request.Text) && string.IsNullOrEmpty(request.Html))
        {
            errors["body"] = ["Provide text, html, or both."];
        }
        else if (Encoding.UTF8.GetByteCount(request.Text ?? string.Empty)
                 + Encoding.UTF8.GetByteCount(request.Html ?? string.Empty) > options.MaxBodyBytes)
        {
            errors["body"] = [$"Text and html together must be at most {options.MaxBodyBytes} bytes."];
        }

        return errors;
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
