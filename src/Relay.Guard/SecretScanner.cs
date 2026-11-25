using System.Text.RegularExpressions;

namespace Relay.Guard;

public enum FindingSeverity
{
    Low,
    Medium,
    High,
}

/// <summary>A suspected secret or PII. <see cref="Sample"/> is masked; the raw value never leaves the scanner.</summary>
public sealed record Finding(string Kind, FindingSeverity Severity, string Sample);

/// <summary>
/// Pattern and entropy checks for credentials and personal data in outgoing mail.
/// Defence in depth: it catches the common shapes, not every encoding of a secret.
/// </summary>
public static partial class SecretScanner
{
    private static readonly (string Kind, FindingSeverity Severity, Regex Pattern)[] Patterns =
    [
        ("private key", FindingSeverity.High, PrivateKey()),
        ("AWS access key", FindingSeverity.High, AwsKey()),
        ("GitHub token", FindingSeverity.High, GitHubToken()),
        ("Slack token", FindingSeverity.High, SlackToken()),
        ("Stripe secret key", FindingSeverity.High, StripeKey()),
        ("Relay API key", FindingSeverity.High, RelayKey()),
        ("JSON web token", FindingSeverity.High, Jwt()),
        ("credential assignment", FindingSeverity.High, CredentialAssignment()),
        ("US social security number", FindingSeverity.Medium, Ssn()),
    ];

    public static IReadOnlyList<Finding> Scan(params string?[] parts)
    {
        var text = string.Join("\n", parts.Where(p => !string.IsNullOrEmpty(p)));
        var findings = new List<Finding>();
        foreach (var (kind, severity, pattern) in Patterns)
        {
            var match = pattern.Match(text);
            if (match.Success)
            {
                findings.Add(new Finding(kind, severity, Mask(match.Value)));
            }
        }

        foreach (Match m in CardCandidate().Matches(text))
        {
            var digits = new string([.. m.Value.Where(char.IsDigit)]);
            if (digits.Length is >= 13 and <= 19 && Luhn(digits))
            {
                findings.Add(new Finding("payment card number", FindingSeverity.Medium, Mask(digits)));
                break;
            }
        }

        if (findings.Count == 0)
        {
            foreach (Match m in TokenCandidate().Matches(text))
            {
                if (LooksRandom(m.Value))
                {
                    findings.Add(new Finding("high-entropy token", FindingSeverity.Low, Mask(m.Value)));
                    break;
                }
            }
        }

        return findings;
    }

    private static string Mask(string value) =>
        value.Length <= 8 ? new string('*', value.Length) : $"{value[..4]}…{value[^2..]} ({value.Length} chars)";

    private static bool Luhn(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    /// <summary>Mixed character classes and high Shannon entropy: how generated secrets look.</summary>
    private static bool LooksRandom(string token)
    {
        var classes = (token.Any(char.IsUpper) ? 1 : 0) + (token.Any(char.IsLower) ? 1 : 0) + (token.Any(char.IsDigit) ? 1 : 0);
        if (classes < 3)
        {
            return false;
        }

        var entropy = token.GroupBy(c => c)
            .Select(g => (double)g.Count() / token.Length)
            .Sum(p => -p * Math.Log2(p));
        return entropy >= 4.0;
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b")]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\b[sr]k_live_[A-Za-z0-9]{16,}\b")]
    private static partial Regex StripeKey();

    [GeneratedRegex(@"\brly_[a-z0-9]{8}_[A-Za-z0-9_-]{20,}")]
    private static partial Regex RelayKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|api[_ -]?key|access[_ -]?token|client[_ -]?secret)\b\s*[:=]\s*(?=\S*[0-9!@#$%^&*_+=])\S{6,}")]
    private static partial Regex CredentialAssignment();

    [GeneratedRegex(@"\b(?!000|666|9\d\d)\d{3}-(?!00)\d{2}-(?!0000)\d{4}\b")]
    private static partial Regex Ssn();

    [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b")]
    private static partial Regex CardCandidate();

    [GeneratedRegex(@"[A-Za-z0-9+/_\-]{24,}")]
    private static partial Regex TokenCandidate();
}
