using Microsoft.Extensions.Time.Testing;
using Relay.Application;
using Shouldly;

namespace Relay.UnitTests;

public class SendMessageHandlerTests
{
    private readonly FakeMessageRepository _repo = new();
    private readonly FakeSuppressionStore _suppressions = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly SendMessageHandler _handler;

    public SendMessageHandlerTests() => _handler = new SendMessageHandler(_repo, _suppressions, new MessagingOptions(), _time);

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

    [Fact]
    public async Task Suppressed_recipient_is_refused_before_anything_is_saved()
    {
        _suppressions.Addresses.Add("blocked@example.test");

        var outcome = await _handler.HandleAsync(
            _tenant, Request() with { To = ["ok@example.test"], Cc = ["Blocked@Example.test"] }, null, default);

        outcome.ShouldBeOfType<SendSuppressed>().Addresses.ShouldBe(["blocked@example.test"]);
        _repo.Saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task Quota_counts_every_recipient()
    {
        _repo.QuotaLimit = 2;

        (await _handler.HandleAsync(_tenant, Request() with { Cc = ["c@example.test"] }, null, default)).ShouldBeOfType<SendAccepted>();
        (await _handler.HandleAsync(_tenant, Request(), null, default)).ShouldBeOfType<SendQuotaExceeded>();
        _repo.QuotaUsed.ShouldBe(2);
    }

    [Fact]
    public async Task Paused_tenant_is_refused()
    {
        _repo.PausedReason = "Bounce rate too high";

        (await _handler.HandleAsync(_tenant, Request(), null, default)).ShouldBeOfType<SendPaused>().Reason.ShouldBe("Bounce rate too high");
    }

    [Fact]
    public async Task Bulk_creates_one_message_per_recipient_and_replays_by_key()
    {
        var bulk = new SendBulkRequest { Recipients = ["a@example.test", "b@example.test", "c@example.test"], Subject = "S", Text = "T" };

        var first = (await _handler.HandleBulkAsync(_tenant, bulk, "batch-1", default)).ShouldBeOfType<SendAccepted>();
        var again = (await _handler.HandleBulkAsync(_tenant, bulk, "batch-1", default)).ShouldBeOfType<SendAccepted>();

        first.Messages.Select(m => m.To.Single()).ShouldBe(bulk.Recipients);
        first.Messages.Select(m => m.IdempotencyKey).ShouldBe(["batch-1#0", "batch-1#1", "batch-1#2"]);
        again.Replayed.ShouldBeTrue();
        again.Messages.Select(m => m.Id).ShouldBe(first.Messages.Select(m => m.Id), ignoreOrder: true);
        _repo.QuotaUsed.ShouldBe(3);
        (await _handler.HandleBulkAsync(_tenant, bulk with { Subject = "Other" }, "batch-1", default)).ShouldBeOfType<SendIdempotencyConflict>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("new\nline")]
    [InlineData("has#hash")]
    public async Task Malformed_idempotency_key_is_invalid(string key) =>
        (await _handler.HandleAsync(_tenant, Request(), key, default))
            .ShouldBeOfType<SendInvalid>().Errors.ShouldContainKey("Idempotency-Key");
}
