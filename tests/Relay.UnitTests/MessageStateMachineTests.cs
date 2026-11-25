using Relay.Domain;
using Shouldly;

namespace Relay.UnitTests;

public class MessageStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static Message NewMessage() => Message.Accept(
        Guid.CreateVersion7(T0), Guid.NewGuid(),
        new MessageDraft("from@relay.test", ["to@example.test"], [], [], "Hi", "Body", null),
        null, null, T0);

    [Fact]
    public void Accept_starts_in_Accepted_with_one_event()
    {
        var m = NewMessage();

        m.Status.ShouldBe(MessageStatus.Accepted);
        m.Events.ShouldHaveSingleItem().ToStatus.ShouldBe(MessageStatus.Accepted);
    }

    [Fact]
    public void Happy_path_appends_an_event_per_step()
    {
        var m = NewMessage();

        m.Transition(MessageStatus.Sending, T0.AddSeconds(1)).Succeeded.ShouldBeTrue();
        m.Transition(MessageStatus.Sent, T0.AddSeconds(2)).Succeeded.ShouldBeTrue();
        m.Transition(MessageStatus.Delivered, T0.AddSeconds(3)).Succeeded.ShouldBeTrue();

        m.Status.ShouldBe(MessageStatus.Delivered);
        m.UpdatedAt.ShouldBe(T0.AddSeconds(3));
        m.Events.Select(e => e.Sequence).ShouldBe([1, 2, 3, 4]);
        m.Events[^1].FromStatus.ShouldBe(MessageStatus.Sent);
    }

    [Theory]
    [InlineData(MessageStatus.Delivered)]
    [InlineData(MessageStatus.Sent)]
    [InlineData(MessageStatus.Bounced)]
    [InlineData(MessageStatus.Accepted)]
    public void Illegal_move_from_Accepted_is_rejected_and_changes_nothing(MessageStatus to)
    {
        var m = NewMessage();

        var result = m.Transition(to, T0.AddSeconds(1));

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        m.Status.ShouldBe(MessageStatus.Accepted);
        m.Events.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(MessageStatus.Delivered)]
    [InlineData(MessageStatus.Bounced)]
    [InlineData(MessageStatus.DeadLettered)]
    [InlineData(MessageStatus.Rejected)]
    public void Terminal_states_accept_no_further_moves(MessageStatus terminal)
    {
        Message.IsTerminal(terminal).ShouldBeTrue();
        foreach (var to in Enum.GetValues<MessageStatus>())
        {
            Message.CanTransition(terminal, to).ShouldBeFalse($"{terminal} -> {to}");
        }
    }

    [Fact]
    public void Delivered_and_failed_can_never_both_happen()
    {
        Message.CanTransition(MessageStatus.Delivered, MessageStatus.DeadLettered).ShouldBeFalse();
        Message.CanTransition(MessageStatus.DeadLettered, MessageStatus.Delivered).ShouldBeFalse();
    }
}
