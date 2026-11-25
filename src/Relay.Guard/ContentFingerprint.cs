using System.Security.Cryptography;
using System.Text;

namespace Relay.Guard;

/// <summary>
/// Near-duplicate key: lower-cased letters of subject and body only, so a retry that changes
/// whitespace, punctuation, a timestamp or a counter still matches.
/// </summary>
public static class ContentFingerprint
{
    public static string Compute(string? subject, string? text, string? html)
    {
        var letters = new StringBuilder();
        foreach (var c in $"{subject}\n{text}\n{StripTags(html)}")
        {
            if (char.IsLetter(c))
            {
                letters.Append(char.ToLowerInvariant(c));
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(letters.ToString())));
    }

    private static string StripTags(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(html.Length);
        var inTag = false;
        foreach (var c in html)
        {
            inTag = c == '<' || (inTag && c != '>');
            if (!inTag && c != '>')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
