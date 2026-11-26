using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Relay.Application;
using Relay.Domain;
using Relay.Guard;
using Shouldly;

namespace Relay.UnitTests;

// Tests added after mutation testing (bench/run-mutation.sh) showed these limits and branches could
// be changed without any test failing. Most pin a limit from both sides: exactly at it is allowed,
// one past it is not.

public class GuardBoundaryTests
{
    private static readonly HashSet<string> Trusted = new(["maya@example.test"], StringComparer.Ordinal);
    private static readonly GuardFacts NoHistory = new(new HashSet<string>(), 0, new Dictionary<string, int>(), null);
    private static readonly GuardPolicy OpenDomain = new() { AllowedDomains = ["example.test"] };

    private static GuardDecision Evaluate(IReadOnlyList<string> to, string text = "Hello", GuardFacts? facts = null, GuardPolicy? policy = null) =>
        GuardEvaluator.Evaluate(new GuardInput(to, "Subject", text, null, Trusted), facts ?? NoHistory, policy ?? new GuardPolicy());

    private static string[] Addresses(int count, string prefix = "u") =>
        [.. Enumerable.Range(0, count).Select(i => $"{prefix}{i}@example.test")];

    [Fact]
    public void Per_message_cap_allows_exactly_the_cap()
    {
        Evaluate(Addresses(10), policy: OpenDomain).Action.ShouldBe(GuardAction.Allow);
        Evaluate(Addresses(11), policy: OpenDomain).Action.ShouldBe(GuardAction.Block);
    }

    [Fact]
    public void Per_run_cap_counts_distinct_recipients_across_the_run()
    {
        var earlier = new HashSet<string>(Addresses(24, "old"), StringComparer.Ordinal);
        var facts = NoHistory with { RunRecipients = earlier };

        Evaluate(Addresses(1), facts: facts, policy: OpenDomain).Action.ShouldBe(GuardAction.Allow); // 25
        Evaluate(Addresses(2), facts: facts, policy: OpenDomain).Action.ShouldBe(GuardAction.Block); // 26
        Evaluate(["old0@example.test"], facts: NoHistory with { RunRecipients = new HashSet<string>(Addresses(25, "old"), StringComparer.Ordinal) },
            policy: OpenDomain).Action.ShouldBe(GuardAction.Allow); // already counted once
    }

    [Fact]
    public void Hourly_and_per_recipient_caps_allow_the_last_permitted_send()
    {
        Evaluate(["maya@example.test"], facts: NoHistory with { SendsLastHour = 29 }).Action.ShouldBe(GuardAction.Allow);
        Evaluate(["maya@example.test"], facts: NoHistory with { SendsTodayByRecipient = new Dictionary<string, int> { ["maya@example.test"] = 2 } })
            .Action.ShouldBe(GuardAction.Allow);
    }

    [Fact]
    public void Each_extra_untrusted_recipient_adds_risk_up_to_a_block()
    {
        Evaluate(Addresses(2, "x").Select(a => a.Replace("example.test", "evil.test", StringComparison.Ordinal)).ToArray())
            .RiskScore.ShouldBe(60);

        var four = Evaluate(Addresses(4, "x").Select(a => a.Replace("example.test", "evil.test", StringComparison.Ordinal)).ToArray());
        four.RiskScore.ShouldBe(80);
        four.Action.ShouldBe(GuardAction.Block); // exactly the block threshold blocks
        four.Reasons.Count(r => r.Contains("evil.test", StringComparison.Ordinal)).ShouldBe(4);
    }

    [Fact]
    public void The_worst_finding_sets_the_score_and_every_finding_is_a_reason()
    {
        var text = "-----BEGIN " + "OPENSSH PRIVATE KEY----- and card 4111 1111 1111 1111";
        var d = Evaluate(["maya@example.test"], text);

        d.RiskScore.ShouldBe(60); // High (60), not Medium (45)
        d.Reasons.ShouldContain(r => r.Contains("private key", StringComparison.Ordinal));
        d.Reasons.ShouldContain(r => r.Contains("payment card number", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_presets_switch_on_exactly_what_their_names_say()
    {
        new GuardChecks().ShouldBe(new GuardChecks { Caps = true, Provenance = true, SecretScan = true, Duplicates = true });
        GuardChecks.Full.ShouldBe(new GuardChecks());
        GuardChecks.None.ShouldBe(new GuardChecks { Caps = false, Provenance = false, SecretScan = false, Duplicates = false });
        GuardChecks.CapsOnly.ShouldBe(GuardChecks.None with { Caps = true });
        GuardChecks.CapsAndProvenance.ShouldBe(GuardChecks.None with { Caps = true, Provenance = true });
    }

    [Fact]
    public void Duplicate_check_is_on_by_default() =>
        Evaluate(["maya@example.test"], facts: NoHistory with { DuplicateOf = Guid.CreateVersion7() }).Action.ShouldBe(GuardAction.Duplicate);

    [Fact]
    public void Fingerprint_keeps_text_before_the_first_tag() =>
        ContentFingerprint.Compute(null, null, "Hello <b>world</b>").ShouldBe(ContentFingerprint.Compute(null, "Hello world", null));
}

public class SecretScannerBoundaryTests
{
    private static string LuhnValid(string payload)
    {
        for (var check = 0; check <= 9; check++)
        {
            var candidate = payload + check.ToString(CultureInfo.InvariantCulture);
            var sum = 0;
            for (var i = 0; i < candidate.Length; i++)
            {
                var d = candidate[candidate.Length - 1 - i] - '0';
                if (i % 2 == 1)
                {
                    d = d * 2 > 9 ? (d * 2) - 9 : d * 2;
                }

                sum += d;
            }

            if (sum % 10 == 0)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("unreachable");
    }

    [Theory]
    [InlineData(13)]
    [InlineData(19)]
    public void Card_numbers_of_13_and_19_digits_are_found(int length)
    {
        var number = LuhnValid("4" + new string('1', length - 2));
        var finding = SecretScanner.Scan($"card {number} thanks").ShouldHaveSingleItem();

        finding.Kind.ShouldBe("payment card number");
        finding.Sample.ShouldBe($"{number[..4]}…{number[^2..]} ({length} chars)"); // masked, never the number
    }

    [Fact]
    public void Two_card_numbers_give_one_finding() =>
        SecretScanner.Scan("cards 4111 1111 1111 1111 and 5555 5555 5555 4444").Count(f => f.Kind == "payment card number").ShouldBe(1);

    [Fact]
    public void Two_random_tokens_give_one_finding() =>
        SecretScanner.Scan("a Q7fK2mX9pL4vR8sT1wY6zB3n and b H5jD0cG8eA2uN6kM4qW9rS7t").Count.ShouldBe(1);

    [Theory]
    [InlineData("abcdefghijklmnop0123456789qrstuv")] // no upper case
    [InlineData("ABCDEFGHIJKLMNOP0123456789QRSTUV")] // no lower case
    [InlineData("ABCDEFGHIJKLMNOPabcdefghijklmnop")] // no digits
    public void Tokens_need_all_three_character_classes(string token) => SecretScanner.Scan($"token {token}").ShouldBeEmpty();

    [Fact]
    public void Entropy_of_exactly_four_bits_counts_as_random() =>
        SecretScanner.Scan("token ABCDEFabcdef0123ABCDEFabcdef0123").ShouldHaveSingleItem().Kind.ShouldBe("high-entropy token");
}

public class ValidationBoundaryTests
{
    private static readonly MessagingOptions Options = new();

    private static SendMessageRequest Request(int to = 1, int cc = 0, int bcc = 0) => new()
    {
        To = [.. Enumerable.Range(0, to).Select(i => $"t{i}@example.test")],
        Cc = [.. Enumerable.Range(0, cc).Select(i => $"c{i}@example.test")],
        Bcc = [.. Enumerable.Range(0, bcc).Select(i => $"b{i}@example.test")],
        Subject = "Hi",
        Text = "Body",
    };

    [Fact]
    public void Cc_and_bcc_addresses_are_validated()
    {
        MessageValidator.Validate(Request() with { Cc = ["not an address"] }, Options).ShouldContainKey("cc");
        MessageValidator.Validate(Request() with { Bcc = ["x@example.test\r\nBcc: y@evil.test"] }, Options).ShouldContainKey("bcc");
    }

    [Fact]
    public void Recipient_limit_counts_to_cc_and_bcc_together()
    {
        MessageValidator.Validate(Request(to: 20, cc: 20, bcc: 10), Options).ShouldBeEmpty();
        MessageValidator.Validate(Request(to: 20, cc: 20, bcc: 11), Options).ShouldContainKey("recipients");
    }

    [Fact]
    public void Subject_and_body_limits_allow_exactly_the_limit()
    {
        MessageValidator.Validate(Request() with { Subject = new string('s', 998) }, Options).ShouldBeEmpty();
        MessageValidator.Validate(Request() with { Subject = new string('s', 999) }, Options).ShouldContainKey("subject");

        var half = Options.MaxBodyBytes / 2;
        MessageValidator.Validate(Request() with { Text = new string('t', half), Html = new string('h', half) }, Options).ShouldBeEmpty();
        MessageValidator.Validate(Request() with { Text = new string('t', half), Html = new string('h', half + 1) }, Options).ShouldContainKey("body");
        MessageValidator.Validate(Request() with { Text = new string('t', Options.MaxBodyBytes + 1) }, Options).ShouldContainKey("body");
    }

    [Fact]
    public void Bulk_requests_are_validated_like_single_ones()
    {
        SendBulkRequest Bulk(int n) => new() { Recipients = [.. Enumerable.Range(0, n).Select(i => $"r{i}@example.test")], Subject = "Hi", Text = "Body" };

        MessageValidator.Validate(Bulk(1000), Options).ShouldBeEmpty();
        MessageValidator.Validate(Bulk(1001), Options).ShouldContainKey("recipients");
        MessageValidator.Validate(new SendBulkRequest { Subject = "Hi", Text = "Body" }, Options).ShouldContainKey("recipients");
        MessageValidator.Validate(Bulk(1) with { Recipients = ["nope"] }, Options).ShouldContainKey("recipients");
        MessageValidator.Validate(Bulk(1) with { Subject = "Hi\r\nBcc: x@evil.test" }, Options).ShouldContainKey("subject");
    }

    [Fact]
    public void Address_length_limits_are_inclusive()
    {
        var local64 = new string('a', 64);
        var host189 = string.Join('.', Enumerable.Repeat(new string('b', 61), 3)) + ".abc";
        EmailAddress.IsValid($"{local64}@{host189}").ShouldBeTrue(); // 254 characters
        EmailAddress.IsValid($"{local64}@c{host189}").ShouldBeFalse(); // 255
        EmailAddress.IsValid($"{new string('a', 65)}@example.test").ShouldBeFalse();
        EmailAddress.IsValid("ma\u0007ya@example.test").ShouldBeFalse();
    }
}

public class SendHandlerBoundaryTests
{
    private readonly FakeMessageRepository _repo = new();
    private readonly FakeSuppressionStore _suppressions = new();
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly SendMessageHandler _handler;

    public SendHandlerBoundaryTests() =>
        _handler = new SendMessageHandler(_repo, _suppressions, new MessagingOptions(), new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero)));

    private static SendMessageRequest Request() => new() { To = ["to@example.test"], Subject = "Hi", Text = "Body" };

    private static SendBulkRequest Bulk(params string[] to) => new() { Recipients = to, Subject = "Hi", Text = "Body" };

    [Theory]
    [InlineData("!", true)]
    [InlineData("~", true)]
    [InlineData("a b", false)]
    [InlineData("a\u007fb", false)]
    [InlineData("a#b", false)]
    public async Task Idempotency_keys_are_printable_ascii_without_hash(string key, bool ok)
    {
        var outcome = await _handler.HandleAsync(_tenant, Request(), key, default);
        (outcome is SendAccepted).ShouldBe(ok);
    }

    [Fact]
    public async Task Bulk_keys_leave_room_for_the_child_suffix()
    {
        (await _handler.HandleAsync(_tenant, Request(), new string('k', 200), default)).ShouldBeOfType<SendAccepted>();
        (await _handler.HandleAsync(_tenant, Request(), new string('k', 201), default)).ShouldBeOfType<SendInvalid>();
        (await _handler.HandleBulkAsync(_tenant, Bulk("a@example.test"), new string('b', 194), default)).ShouldBeOfType<SendAccepted>();
        (await _handler.HandleBulkAsync(_tenant, Bulk("a@example.test"), new string('c', 195), default)).ShouldBeOfType<SendInvalid>();
    }

    [Fact]
    public async Task Keys_and_request_hashes_are_stored_only_when_a_key_is_given()
    {
        var single = (await _handler.HandleAsync(_tenant, Request(), "single", default)).ShouldBeOfType<SendAccepted>().Message;
        single.IdempotencyKey.ShouldBe("single");
        single.RequestHash.ShouldNotBeNull();

        var bulk = (await _handler.HandleBulkAsync(_tenant, Bulk("a@example.test", "b@example.test"), "batch", default)).ShouldBeOfType<SendAccepted>();
        bulk.Messages.Select(m => m.IdempotencyKey).ShouldBe(["batch#0", "batch#1"]);

        var keyless = (await _handler.HandleAsync(_tenant, Request(), null, default)).ShouldBeOfType<SendAccepted>().Message;
        keyless.IdempotencyKey.ShouldBeNull();
        keyless.RequestHash.ShouldBeNull();
    }

    [Fact]
    public async Task Explicit_sender_and_cc_are_kept()
    {
        var m = (await _handler.HandleAsync(_tenant, Request() with { From = "ops@example.test", Cc = ["cc@example.test"], Bcc = ["bcc@example.test"] }, null, default))
            .ShouldBeOfType<SendAccepted>().Message;
        m.From.ShouldBe("ops@example.test");
        m.Cc.ShouldBe(["cc@example.test"]);
        m.Bcc.ShouldBe(["bcc@example.test"]);

        var b = (await _handler.HandleBulkAsync(_tenant, Bulk("a@example.test") with { From = "ops@example.test" }, null, default))
            .ShouldBeOfType<SendAccepted>().Message;
        b.From.ShouldBe("ops@example.test");
        (await _handler.HandleBulkAsync(_tenant, Bulk("a@example.test"), null, default)).ShouldBeOfType<SendAccepted>().Message.From
            .ShouldBe("noreply@relay.test");
    }

    [Fact]
    public async Task Suppression_covers_cc_and_bcc()
    {
        _suppressions.Addresses.Add("cc@example.test");
        (await _handler.HandleAsync(_tenant, Request() with { Cc = ["cc@example.test"] }, null, default)).ShouldBeOfType<SendSuppressed>();
        _repo.Saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task Agent_context_tags_the_message_and_can_hold_it()
    {
        var run = Guid.CreateVersion7();
        var sent = (await _handler.HandleAsync(_tenant, Request(), null, new SendContext(run, "hash-1", null), default)).ShouldBeOfType<SendAccepted>().Message;
        sent.AgentRunId.ShouldBe(run);
        sent.ContentHash.ShouldBe("hash-1");
        sent.Status.ShouldBe(MessageStatus.Accepted);

        var held = (await _handler.HandleAsync(_tenant, Request(), null, new SendContext(run, "hash-2", "needs approval"), default))
            .ShouldBeOfType<SendAccepted>().Message;
        held.Status.ShouldBe(MessageStatus.Held);
    }

    [Fact]
    public async Task A_replay_matches_only_if_every_stored_child_matches()
    {
        await _handler.HandleBulkAsync(_tenant, Bulk("a@example.test", "b@example.test"), "batch", default);
        _repo.Saved.Add(Message.Accept(Guid.CreateVersion7(), _tenant,
            new MessageDraft("noreply@relay.test", ["c@example.test"], [], [], "Hi", "Body", null), "batch#2", "a-different-hash", "relay.test",
            DateTimeOffset.UnixEpoch));

        (await _handler.HandleBulkAsync(_tenant, Bulk("a@example.test", "b@example.test"), "batch", default)).ShouldBeOfType<SendIdempotencyConflict>();
    }
}

public class DomainBoundaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);

    private static Message NewMessage() => Message.Accept(Guid.CreateVersion7(), Guid.CreateVersion7(),
        new MessageDraft("from@example.test", ["to@example.test"], [], [], "Subject", "Body", null), null, null, "relay.test", T0);

    [Fact]
    public void Pausing_keeps_the_first_reason_until_resumed()
    {
        var tenant = Tenant.Create(Guid.CreateVersion7(), "acme", 100, T0);
        tenant.IsSendingPaused.ShouldBeFalse();

        tenant.PauseSending("bounces", T0);
        tenant.PauseSending("complaints", T0.AddHours(1));
        tenant.IsSendingPaused.ShouldBeTrue();
        tenant.PauseReason.ShouldBe("bounces");
        tenant.SendingPausedAt.ShouldBe(T0);

        tenant.ResumeSending();
        tenant.IsSendingPaused.ShouldBeFalse();
        tenant.PauseReason.ShouldBeNull();
    }

    [Fact]
    public void Deactivated_tenants_are_inactive()
    {
        var tenant = Tenant.Create(Guid.CreateVersion7(), "acme", 100, T0);
        tenant.IsActive.ShouldBeTrue();
        tenant.Deactivate();
        tenant.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void Revoking_twice_keeps_the_first_time()
    {
        var key = ApiKey.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "k", "rly_abc", "hash", ["messages:send"], T0);
        key.Revoke(T0.AddMinutes(1));
        key.Revoke(T0.AddMinutes(2));
        key.RevokedAt.ShouldBe(T0.AddMinutes(1));
        key.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void Transition_errors_only_on_refusal()
    {
        var m = NewMessage();
        m.Transition(MessageStatus.Sending, T0).Error.ShouldBeNull();
        m.Transition(MessageStatus.Accepted, T0).Error.ShouldBeNull();
        m.Transition(MessageStatus.Delivered, T0).Error.ShouldBe("Illegal transition Accepted -> Delivered.");
    }

    [Fact]
    public void Annotations_add_a_timeline_entry_without_changing_state()
    {
        var m = NewMessage();
        m.Annotate("complaint received", T0.AddDays(1));

        m.Status.ShouldBe(MessageStatus.Accepted);
        m.Events.Count.ShouldBe(2);
        m.Events[^1].ShouldSatisfyAllConditions(
            e => e.Sequence.ShouldBe(2),
            e => e.FromStatus.ShouldBe(MessageStatus.Accepted),
            e => e.ToStatus.ShouldBe(MessageStatus.Accepted),
            e => e.Detail.ShouldBe("complaint received"));
        m.UpdatedAt.ShouldBe(T0.AddDays(1));
    }

    [Fact]
    public void Agent_context_and_provider_are_recorded()
    {
        var m = NewMessage();
        var run = Guid.CreateVersion7();
        m.AttachAgentContext(run, "content");
        m.RecordProvider("smtp", "pid-1");

        m.AgentRunId.ShouldBe(run);
        m.ContentHash.ShouldBe("content");
        m.ProviderName.ShouldBe("smtp");
        m.ProviderMessageId.ShouldBe("pid-1");
    }
}
