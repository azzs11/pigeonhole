using System.Security.Cryptography;
using Relay.Guard;
using Shouldly;

namespace Relay.UnitTests;

/// <summary>Fake secrets are assembled at runtime so no credential-shaped literal is committed.</summary>
public class SecretScannerTests
{
    private static string Rand(string alphabet, int n) => RandomNumberGenerator.GetString(alphabet, n);

    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const string Mixed = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    // Only the kind is theory data: the fake secret is built inside the test, so test names stay the
    // same from discovery to run (mutation testing needs that) while the value is still random.
    public static TheoryData<string> Kinds() =>
    [
        "AWS access key", "GitHub token", "private key", "Stripe secret key",
        "credential assignment", "JSON web token", "US social security number",
    ];

    private static string FakeSecret(string kind) => kind switch
    {
        "AWS access key" => "key " + "AK" + "IA" + Rand(Upper, 16),
        "GitHub token" => "gh" + "p_" + Rand(Mixed, 36),
        "private key" => "-----BEGIN " + "OPENSSH PRIVATE KEY-----",
        "Stripe secret key" => "sk" + "_live_" + Rand(Mixed, 24),
        "credential assignment" => "password: " + Rand(Mixed, 8) + "9!",
        "JSON web token" => "eyJ" + Rand(Mixed, 12) + ".eyJ" + Rand(Mixed, 20) + "." + Rand(Mixed, 16),
        "US social security number" => "SSN 123-45-6789",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Detects_common_secret_shapes_and_masks_them(string kind)
    {
        var text = FakeSecret(kind);
        var finding = SecretScanner.Scan($"Hello, {text} thanks").ShouldHaveSingleItem();

        finding.Kind.ShouldBe(kind);
        finding.Sample.ShouldNotBe(text);
    }

    [Fact]
    public void Card_numbers_need_a_valid_luhn_checksum()
    {
        SecretScanner.Scan("card 4111 1111 1111 1111").ShouldContain(f => f.Kind == "payment card number");
        SecretScanner.Scan("card 4111 1111 1111 1112").ShouldBeEmpty();
    }

    [Fact]
    public void High_entropy_tokens_are_flagged_low()
    {
        var token = Rand(Mixed, 40);
        SecretScanner.Scan($"token {token}").ShouldContain(f => f.Severity == FindingSeverity.Low);
    }

    [Theory]
    [InlineData("Your order INV-2026-48213 ships on 12 Dec.")]
    [InlineData("Call me on +1 415-555-0134 if the slot moves.")]
    [InlineData("To reset your password: use the link on the sign-in page.")]
    [InlineData("Tracking number 1ZA8F3K29XQ7M4P1BB for the replacement laptop.")]
    [InlineData("Join here: https://meet.example.test/abc-defg-hij")]
    [InlineData("The total came to $1,482.35; receipt to follow.")]
    public void Ordinary_business_text_is_clean(string text) => SecretScanner.Scan(text).ShouldBeEmpty();
}

public class ContentFingerprintTests
{
    [Fact]
    public void Ignores_case_whitespace_punctuation_digits_and_tags() =>
        ContentFingerprint.Compute("Reminder", "Hi Sam, see you at 10:00!", null)
            .ShouldBe(ContentFingerprint.Compute("reminder ", null, "<p>Hi   sam see you at 11:30</p>"));

    [Fact]
    public void Different_words_differ() =>
        ContentFingerprint.Compute("Reminder", "see you tomorrow", null)
            .ShouldNotBe(ContentFingerprint.Compute("Reminder", "see you on friday", null));
}

public class GuardEvaluatorTests
{
    private static readonly HashSet<string> Trusted = new(["maya@example.test"], StringComparer.Ordinal);
    private static readonly GuardFacts NoHistory = new(new HashSet<string>(), 0, new Dictionary<string, int>(), null);

    private static GuardDecision Evaluate(string[] to, string text = "Hello", GuardFacts? facts = null, GuardPolicy? policy = null) =>
        GuardEvaluator.Evaluate(new GuardInput(to, "Subject", text, null, Trusted), facts ?? NoHistory, policy ?? new GuardPolicy());

    [Fact]
    public void Trusted_recipient_with_clean_content_is_allowed() =>
        Evaluate(["maya@example.test"]).Action.ShouldBe(GuardAction.Allow);

    [Fact]
    public void Untrusted_recipient_is_held_for_approval()
    {
        var d = Evaluate(["attacker@evil.test"]);
        d.Action.ShouldBe(GuardAction.Hold);
        d.Recipients.ShouldHaveSingleItem().Provenance.ShouldBe(Provenance.Untrusted);
    }

    [Fact]
    public void Untrusted_recipient_plus_secret_is_blocked() =>
        Evaluate(["attacker@evil.test"], "key " + "AK" + "IA" + RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567", 16))
            .Action.ShouldBe(GuardAction.Block);

    [Fact]
    public void Allowed_domain_counts_as_trusted() =>
        Evaluate(["ops@partner.test"], policy: new GuardPolicy { AllowedDomains = ["partner.test"] }).Action.ShouldBe(GuardAction.Allow);

    [Fact]
    public void Caps_block_regardless_of_score()
    {
        Evaluate(["maya@example.test"], facts: NoHistory with { SendsLastHour = 30 }).Action.ShouldBe(GuardAction.Block);
        Evaluate(["maya@example.test"], facts: NoHistory with { SendsTodayByRecipient = new Dictionary<string, int> { ["maya@example.test"] = 3 } })
            .Action.ShouldBe(GuardAction.Block);
        Evaluate([.. Enumerable.Range(0, 11).Select(i => $"u{i}@example.test")], policy: new GuardPolicy { AllowedDomains = ["example.test"] })
            .Action.ShouldBe(GuardAction.Block);
    }

    [Fact]
    public void Near_duplicate_returns_the_original()
    {
        var original = Guid.NewGuid();
        var d = Evaluate(["maya@example.test"], facts: NoHistory with { DuplicateOf = original });
        d.Action.ShouldBe(GuardAction.Duplicate);
        d.DuplicateOf.ShouldBe(original);
    }

    [Fact]
    public void With_no_checks_everything_is_allowed() =>
        Evaluate(["attacker@evil.test"], facts: NoHistory with { SendsLastHour = 999 }, policy: new GuardPolicy { Checks = GuardChecks.None })
            .Action.ShouldBe(GuardAction.Allow);
}
