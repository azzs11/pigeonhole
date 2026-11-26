using System.Globalization;
using FsCheck;
using FsCheck.Fluent;
using Relay.Application;
using Relay.Domain;
using Relay.Guard;
using Shouldly;

namespace Relay.UnitTests;

/// <summary>
/// Property-based tests: instead of a handful of examples, FsCheck generates thousands of inputs
/// (and shrinks any failure to a minimal case). Each property is an invariant the system relies on.
/// </summary>
public class PropertyTests
{
    private static readonly Config Thorough = Config.QuickThrowOnFailure.WithMaxTest(2_000);
    private static readonly MessageStatus[] AllStatuses = Enum.GetValues<MessageStatus>();
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);

    private static Message NewMessage() =>
        Message.Accept(Guid.CreateVersion7(), Guid.CreateVersion7(),
            new MessageDraft("from@example.test", ["to@example.test"], [], [], "Subject", "Body", null), null, null, "relay.test", T0);

    [Fact]
    public void Any_sequence_of_transition_attempts_keeps_the_state_machine_consistent()
    {
        var attempts = Gen.ArrayOf(Gen.Elements(AllStatuses)).Select(a => a.Take(40).ToArray());
        Check.One(Thorough, Prop.ForAll(Arb.From(attempts), targets =>
        {
            var message = NewMessage();
            var successes = 0;
            var terminalReached = false;
            foreach (var to in targets)
            {
                var before = message.Status;
                var result = message.Transition(to, T0.AddSeconds(successes + 1));
                if (result.Succeeded != Message.CanTransition(before, to))
                {
                    return false; // the result must agree with the transition table
                }

                if (terminalReached && result.Succeeded)
                {
                    return false; // nothing leaves a terminal state
                }

                if (result.Succeeded)
                {
                    successes++;
                }
                else if (message.Status != before)
                {
                    return false; // a refused transition changes nothing
                }

                terminalReached |= Message.IsTerminal(message.Status);
            }

            var events = message.Events;
            return events.Count == successes + 1
                && events.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, events.Count))
                && events.Skip(1).Zip(events, (e, prev) => e.FromStatus == prev.ToStatus).All(x => x)
                && events[^1].ToStatus == message.Status;
        }));
    }

    [Fact]
    public void Sent_is_only_reachable_through_a_worker_claim_or_reconciliation()
    {
        // "Nothing is sent without a worker holding it": the only edges into Sent come from Sending or Unknown.
        foreach (var from in AllStatuses)
        {
            Message.CanTransition(from, MessageStatus.Sent).ShouldBe(from is MessageStatus.Sending or MessageStatus.Unknown, $"from {from}");
        }

        foreach (var status in AllStatuses)
        {
            Message.IsTerminal(status).ShouldBe(!AllStatuses.Any(t => Message.CanTransition(status, t)), $"{status}");
        }
    }

    [Fact]
    public void Backoff_stays_within_twenty_percent_of_the_capped_exponential()
    {
        var gen = from attempt in Gen.Choose(1, 40)
                  from jitter in Gen.Choose(0, 1_000_000).Select(j => j / 1_000_000.0)
                  from baseSeconds in Gen.Choose(1, 120)
                  from maxSeconds in Gen.Choose(60, 7_200)
                  select (attempt, jitter, baseSeconds, maxSeconds);
        Check.One(Thorough, Prop.ForAll(Arb.From(gen), x =>
        {
            var options = new DeliveryOptions { BackoffBaseSeconds = x.baseSeconds, BackoffMaxSeconds = x.maxSeconds };
            var nominal = Math.Min(x.baseSeconds * Math.Pow(2, Math.Min(x.attempt - 1, 20)), x.maxSeconds);
            var delay = Backoff.ForAttempt(x.attempt, options, x.jitter).TotalSeconds;
            return delay >= (nominal * 0.8) - 1e-6 && delay <= (nominal * 1.2) + 1e-6 && delay <= (x.maxSeconds * 1.2) + 1e-6;
        }));
    }

    [Fact]
    public void Fingerprint_ignores_case_spacing_digits_and_punctuation_but_not_letters()
    {
        var words = Gen.ArrayOf(Gen.Elements("invoice", "attached", "please", "review", "quote", "desk", "order", "Maya")).Select(w => w.Take(12).ToArray());
        var noise = Gen.ArrayOf(Gen.Elements(" ", "  ", "\n", "\t", ",", ".", "!", "1", "42", "-", "—")).Select(n => n.Take(12).ToArray());
        var gen = from w in words.Where(w => w.Length > 0)
                  from n in noise
                  select (w, n);
        Check.One(Thorough, Prop.ForAll(Arb.From(gen), x =>
        {
            var plain = string.Join(' ', x.w);
            var noisy = string.Concat(x.w.Select((word, i) => (i % 2 == 0 ? word.ToUpperInvariant() : word) + (i < x.n.Length ? x.n[i] : " ")));
            var same = ContentFingerprint.Compute("Hi", plain, null) == ContentFingerprint.Compute("hi!", noisy, null);
            var changed = ContentFingerprint.Compute("Hi", plain + "x", null) != ContentFingerprint.Compute("Hi", plain, null);
            return same && changed;
        }));
    }

    [Fact]
    public void Scanner_never_throws_and_always_finds_a_luhn_valid_card_number()
    {
        var digits = Gen.ArrayOf(Gen.Choose(0, 9)).Select(d => d.Take(15).ToArray()).Where(d => d.Length == 15 && d[0] != 0);
        var filler = Gen.Elements("Card on file: ", "pay with ", "number ", string.Empty);
        var gen = from d in digits
                  from f in filler
                  select (d, f);
        Check.One(Thorough, Prop.ForAll(Arb.From(gen), x =>
        {
            var number = string.Concat(x.d.Select(d => d.ToString(CultureInfo.InvariantCulture))) + LuhnCheckDigit(x.d).ToString(CultureInfo.InvariantCulture);
            return SecretScanner.Scan($"{x.f}{number} thanks").Any(f => f.Kind == "payment card number");
        }));

        Check.One(Thorough, Prop.ForAll(Arb.From(Gen.ArrayOf(Gen.Choose(0, 0xFFFF).Select(c => (char)c))), chars =>
        {
            SecretScanner.Scan(new string(chars), null, string.Empty); // arbitrary UTF-16, including lone surrogates
            return true;
        }));
    }

    [Fact]
    public void Any_control_character_in_a_subject_is_rejected()
    {
        var gen = from prefix in Gen.Elements("Hello", "Invoice", string.Empty)
                  from control in Gen.Choose(0, 31).Select(c => (char)c)
                  from suffix in Gen.Elements("Bcc: attacker@evil.test", "world", string.Empty)
                  select $"{prefix}{control}{suffix}";
        Check.One(Thorough, Prop.ForAll(Arb.From(gen), subject =>
        {
            var errors = MessageValidator.Validate(new SendMessageRequest { To = ["a@example.test"], Subject = subject, Text = "x" }, new MessagingOptions());
            return errors.ContainsKey("subject");
        }));
    }

    [Fact]
    public void Header_injection_through_an_address_is_rejected()
    {
        var gen = from local in Gen.Elements("maya", "a.b", "ops")
                  from breaker in Gen.Elements("\r\n", "\n", "\r", "\0", "\r\nBcc: x@evil.test")
                  from position in Gen.Choose(0, 2)
                  select position switch
                  {
                      0 => $"{breaker}{local}@example.test",
                      1 => $"{local}{breaker}@example.test",
                      _ => $"{local}@example.test{breaker}",
                  };
        Check.One(Thorough, Prop.ForAll(Arb.From(gen), address => !EmailAddress.IsValid(address)));
    }

    private static int LuhnCheckDigit(int[] payload)
    {
        var sum = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            // Doubling starts from the digit next to the (future) check digit, i.e. the last payload digit.
            var d = payload[payload.Length - 1 - i];
            if (i % 2 == 0)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return (10 - (sum % 10)) % 10;
    }
}
