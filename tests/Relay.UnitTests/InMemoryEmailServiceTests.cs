using Relay.Application;
using Relay.Domain;

namespace Relay.UnitTests;

/// <summary>
/// Characterisation tests for the Phase 0 in-memory service. They pin current
/// behaviour so the Phase 1 rewrite can be checked against it.
/// </summary>
public class InMemoryEmailServiceTests
{
    private readonly InMemoryEmailService _service = new();

    private static SendEmailRequest ValidRequest() => new()
    {
        To = "recipient@example.test",
        Subject = "Hello",
        Body = "<p>Hi</p>",
    };

    [Fact]
    public async Task SendEmailAsync_rejects_missing_recipient()
    {
        var request = ValidRequest();
        request.To = " ";

        await Assert.ThrowsAsync<ArgumentException>(() => _service.SendEmailAsync(request, Guid.NewGuid()));
    }

    [Fact]
    public async Task SendEmailAsync_rejects_missing_subject()
    {
        var request = ValidRequest();
        request.Subject = "";

        await Assert.ThrowsAsync<ArgumentException>(() => _service.SendEmailAsync(request, Guid.NewGuid()));
    }

    [Fact]
    public async Task SendEmailAsync_records_message_as_pending_for_the_caller()
    {
        var userId = Guid.NewGuid();

        var response = await _service.SendEmailAsync(ValidRequest(), userId);
        var stored = await _service.GetEmailByIdAsync(response.Id);

        Assert.NotNull(stored);
        Assert.Equal(userId, stored.UserId);
        Assert.Equal(EmailStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task SendBulkEmailAsync_creates_one_message_per_recipient()
    {
        var request = new SendBulkEmailRequest
        {
            To = ["a@example.test", "b@example.test", "c@example.test"],
            Subject = "Bulk",
            Body = "Body",
        };

        var responses = await _service.SendBulkEmailAsync(request, Guid.NewGuid());

        Assert.Equal(3, responses.Count);
        Assert.Equal(3, responses.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task GetEmailsByUserIdAsync_returns_only_that_users_messages()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await _service.SendEmailAsync(ValidRequest(), mine);
        await _service.SendEmailAsync(ValidRequest(), theirs);

        var emails = await _service.GetEmailsByUserIdAsync(mine);

        Assert.Single(emails);
        Assert.All(emails, e => Assert.Equal(mine, e.UserId));
    }

    [Fact]
    public async Task UpdateEmailStatusAsync_sets_sent_timestamp()
    {
        var response = await _service.SendEmailAsync(ValidRequest(), Guid.NewGuid());

        var updated = await _service.UpdateEmailStatusAsync(response.Id, EmailStatus.Sent);
        var stored = await _service.GetEmailByIdAsync(response.Id);

        Assert.True(updated);
        Assert.Equal(EmailStatus.Sent, stored!.Status);
        Assert.NotNull(stored.SentAt);
    }
}
