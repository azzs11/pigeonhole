using System.Net.Mail;

namespace Relay.Application;

public static class EmailAddress
{
    public const int MaxLength = 254;

    /// <summary>
    /// A bare address (no display name), no control characters (blocks CR/LF header
    /// injection), at most 254 characters, with a 64-character local part.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength || value.Any(char.IsControl))
        {
            return false;
        }

        if (!MailAddress.TryCreate(value, out var parsed)
            || !string.Equals(parsed.Address, value, StringComparison.Ordinal)
            || parsed.DisplayName.Length != 0)
        {
            return false;
        }

        return parsed.User.Length <= 64 && parsed.Host.Contains('.', StringComparison.Ordinal);
    }
}
