using System.Security.Cryptography;
using System.Text.Json;
using Relay.Domain;

namespace Relay.Application;

public sealed class SendMessageHandler(IMessageRepository messages, MessagingOptions options, TimeProvider time)
{
    public async Task<SendOutcome> HandleAsync(
        Guid tenantId, SendMessageRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = MessageValidator.Validate(request, options);
        if (idempotencyKey is not null
            && (idempotencyKey.Length == 0 || idempotencyKey.Length > options.MaxIdempotencyKeyLength
                || idempotencyKey.Any(c => c is < '!' or > '~')))
        {
            errors["Idempotency-Key"] = [$"Must be 1-{options.MaxIdempotencyKeyLength} printable ASCII characters."];
        }

        if (errors.Count > 0)
        {
            return new SendInvalid(errors);
        }

        var draft = new MessageDraft(
            request.From ?? options.DefaultFrom,
            request.To!,
            request.Cc ?? [],
            request.Bcc ?? [],
            request.Subject!,
            request.Text,
            request.Html);
        var requestHash = idempotencyKey is null ? null : Hash(draft);

        if (idempotencyKey is not null)
        {
            var existing = await messages.FindByIdempotencyKeyAsync(tenantId, idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                return Replay(existing, requestHash!);
            }
        }

        var now = time.GetUtcNow();
        var message = Message.Accept(Guid.CreateVersion7(now), tenantId, draft, idempotencyKey, requestHash, options.MessageIdDomain, now);
        var winner = await messages.AddAcceptedAsync(message, cancellationToken);

        // A concurrent request with the same key got there first.
        return winner is null ? new SendAccepted(message, Replayed: false) : Replay(winner, requestHash!);
    }

    private static SendOutcome Replay(Message existing, string requestHash) =>
        string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
            ? new SendAccepted(existing, Replayed: true)
            : new SendIdempotencyConflict();

    private static string Hash(MessageDraft draft) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(draft)));
}
