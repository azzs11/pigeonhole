using Relay.Application;
using Relay.Domain;
using Relay.Infrastructure.Observability;
using Shouldly;

namespace Relay.UnitTests;

public class MessageExplainerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static Message NewMessage() => Message.Accept(Guid.CreateVersion7(T0), Guid.NewGuid(),
        new MessageDraft("from@relay.test", ["to@example.test"], [], [], "Hi", "Body", null), null, null, "relay.test", T0);

    [Fact]
    public void Every_step_gets_a_plain_explanation_and_the_summary_reflects_the_outcome()
    {
        var m = NewMessage();
        m.Transition(MessageStatus.Sending, T0, "Attempt 1");
        m.Transition(MessageStatus.Unknown, T0, "Timed out after 30s");
        m.Transition(MessageStatus.Sent, T0, "Reconciled: ses reported Delivery");
        m.Transition(MessageStatus.Delivered, T0, "Delivered (ses)");
        m.RecordProvider("ses", "ses-1");

        var explanation = MessageExplainer.Explain(m);

        explanation.Steps.Count.ShouldBe(5);
        explanation.Steps[2].Explanation.ShouldContain("not resent blindly");
        explanation.Steps[3].Explanation.ShouldContain("no resend is needed");
        explanation.Summary.ShouldBe("Delivered via ses after 1 attempt.");
    }

    [Theory]
    [InlineData(MessageStatus.Unknown, "Outcome unknown")]
    [InlineData(MessageStatus.DeadLettered, "Abandoned")]
    public void Summary_names_what_to_expect(MessageStatus status, string expected)
    {
        var m = NewMessage();
        m.Transition(MessageStatus.Sending, T0);
        m.Transition(status, T0, "detail");

        MessageExplainer.Explain(m).Summary.ShouldStartWith(expected);
    }
}

public class EmailMaskingTests
{
    [Theory]
    [InlineData("Sent to alice.smith@example.com", "Sent to a***@example.com")]
    [InlineData("to: [b@x.test, carol@mail.example.org]", "to: [b***@x.test, c***@mail.example.org]")]
    [InlineData("no address here", "no address here")]
    public void Addresses_are_masked_keeping_the_domain(string input, string expected) =>
        EmailMaskingEnricher.Mask(input).ShouldBe(expected);

    [Fact]
    public void Enricher_masks_scalar_and_nested_properties()
    {
        var logEvent = new Serilog.Events.LogEvent(DateTimeOffset.UtcNow, Serilog.Events.LogEventLevel.Information, null,
            new Serilog.Parsing.MessageTemplateParser().Parse("{To} {Many}"),
            [
                new Serilog.Events.LogEventProperty("To", new Serilog.Events.ScalarValue("bob@example.com")),
                new Serilog.Events.LogEventProperty("Many", new Serilog.Events.SequenceValue(
                    [new Serilog.Events.ScalarValue("dave@example.com")])),
            ]);

        new EmailMaskingEnricher().Enrich(logEvent, null!);

        logEvent.RenderMessage().ShouldBe("\"b***@example.com\" [\"d***@example.com\"]");
    }
}
