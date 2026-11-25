using System.Security.Cryptography;
using System.Text.Json;
using Relay.Domain;

namespace Relay.Application;

public sealed class SendMessageHandler(
    IMessageRepository messages, ISuppressionStore suppressions, MessagingOptions options, TimeProvider time)
{
    public Task<SendOutcome> HandleAsync(
        Guid tenantId, SendMessageRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = MessageValidator.Validate(request, options);
        CheckKey(idempotencyKey, options.MaxIdempotencyKeyLength, errors);
        if (errors.Count > 0)
        {
            return Task.FromResult<SendOutcome>(new SendInvalid(errors));
        }

        var draft = new MessageDraft(request.From ?? options.DefaultFrom, request.To!, request.Cc ?? [], request.Bcc ?? [],
            request.Subject!, request.Text, request.Html);
        return AcceptAsync(tenantId, [draft], Hash(draft), idempotencyKey, bulk: false, cancellationToken);
    }

    public Task<SendOutcome> HandleBulkAsync(
        Guid tenantId, SendBulkRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = MessageValidator.Validate(request, options);

        // Bulk messages get keys "{key}#{index}", so leave room for the suffix.
        CheckKey(idempotencyKey, options.MaxIdempotencyKeyLength - 6, errors);
        if (errors.Count > 0)
        {
            return Task.FromResult<SendOutcome>(new SendInvalid(errors));
        }

        var from = request.From ?? options.DefaultFrom;
        var drafts = request.Recipients!
            .Select(r => new MessageDraft(from, [r], [], [], request.Subject!, request.Text, request.Html))
            .ToList();
        var hash = Hash(new { from, request.Recipients, request.Subject, request.Text, request.Html });
        return AcceptAsync(tenantId, drafts, hash, idempotencyKey, bulk: true, cancellationToken);
    }

    private async Task<SendOutcome> AcceptAsync(
        Guid tenantId, IReadOnlyList<MessageDraft> drafts, string requestHash, string? key, bool bulk, CancellationToken cancellationToken)
    {
        if (key is not null)
        {
            var existing = await messages.FindByIdempotencyKeyAsync(tenantId, key, cancellationToken);
            if (existing.Count > 0)
            {
                return Replay(existing, requestHash);
            }
        }

        var recipients = drafts.SelectMany(d => d.To.Concat(d.Cc).Concat(d.Bcc)).ToList();
        var suppressed = await suppressions.FindSuppressedAsync(tenantId, recipients, cancellationToken);
        if (suppressed.Count > 0)
        {
            return new SendSuppressed(suppressed);
        }

        var now = time.GetUtcNow();
        var accepted = drafts.Select((draft, i) => Message.Accept(
            Guid.CreateVersion7(now), tenantId, draft,
            key is null ? null : bulk ? $"{key}#{i}" : key,
            key is null ? null : requestHash, options.MessageIdDomain, now)).ToList();

        return await messages.AddAcceptedAsync(accepted, recipients.Count, cancellationToken) switch
        {
            AddSucceeded => new SendAccepted(accepted, Replayed: false),
            AddDuplicate duplicate => Replay(duplicate.Existing, requestHash),
            AddQuotaExceeded quota => new SendQuotaExceeded(quota.RetryAfter),
            AddPaused paused => new SendPaused(paused.Reason),
            var other => throw new InvalidOperationException($"Unhandled result {other.GetType().Name}."),
        };
    }

    private static void CheckKey(string? key, int maxLength, Dictionary<string, string[]> errors)
    {
        if (key is not null && (key.Length == 0 || key.Length > maxLength || key.Any(c => c is < '!' or > '~' or '#')))
        {
            errors["Idempotency-Key"] = [$"Must be 1-{maxLength} printable ASCII characters, excluding '#'."];
        }
    }

    private static SendOutcome Replay(IReadOnlyList<Message> existing, string requestHash) =>
        existing.All(m => string.Equals(m.RequestHash, requestHash, StringComparison.Ordinal))
            ? new SendAccepted(existing, Replayed: true)
            : new SendIdempotencyConflict();

    private static string Hash<T>(T value) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
