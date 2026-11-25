using System.Net.Mail;

namespace Relay.Application;

public static class EmailAddress
{
    public const int MaxLength = 254;

    /// <summary>Canonical form for suppression matching: trimmed, lower-case.</summary>
#pragma warning disable CA1308 // addresses are conventionally lower-cased; this is a key, not display text
    public static string Normalize(string address) => (address ?? string.Empty).Trim().ToLowerInvariant();
#pragma warning restore CA1308

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
