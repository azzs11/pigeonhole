using Microsoft.Extensions.Time.Testing;
using Relay.Application;
using Shouldly;

namespace Relay.UnitTests;

public class SendMessageHandlerTests
{
    private readonly FakeMessageRepository _repo = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly SendMessageHandler _handler;

    public SendMessageHandlerTests() => _handler = new SendMessageHandler(_repo, new MessagingOptions(), _time);

    private static SendMessageRequest Request(string subject = "Hi") =>
        new() { To = ["to@example.test"], Subject = subject, Text = "Body" };

    [Fact]
    public async Task Uses_default_sender_and_injected_clock()
    {
        var outcome = (await _handler.HandleAsync(_tenant, Request(), null, default)).ShouldBeOfType<SendAccepted>();

        outcome.Message.From.ShouldBe("noreply@relay.test");
        outcome.Message.CreatedAt.ShouldBe(_time.GetUtcNow());
        outcome.Message.Id.Version.ShouldBe(7);
    }

    [Fact]
    public async Task Same_key_and_body_replays_the_first_message()
    {
        var first = (await _handler.HandleAsync(_tenant, Request(), "key-1", default)).ShouldBeOfType<SendAccepted>();
        var second = (await _handler.HandleAsync(_tenant, Request(), "key-1", default)).ShouldBeOfType<SendAccepted>();

        second.Message.Id.ShouldBe(first.Message.Id);
        second.Replayed.ShouldBeTrue();
        _repo.Saved.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Same_key_with_different_body_is_a_conflict()
    {
        await _handler.HandleAsync(_tenant, Request("A"), "key-1", default);

        (await _handler.HandleAsync(_tenant, Request("B"), "key-1", default)).ShouldBeOfType<SendIdempotencyConflict>();
    }

    [Fact]
    public async Task Keys_are_scoped_per_tenant()
    {
        await _handler.HandleAsync(_tenant, Request(), "key-1", default);

        (await _handler.HandleAsync(Guid.NewGuid(), Request(), "key-1", default))
            .ShouldBeOfType<SendAccepted>().Replayed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("new\nline")]
    public async Task Malformed_idempotency_key_is_invalid(string key) =>
        (await _handler.HandleAsync(_tenant, Request(), key, default))
            .ShouldBeOfType<SendInvalid>().Errors.ShouldContainKey("Idempotency-Key");
}
