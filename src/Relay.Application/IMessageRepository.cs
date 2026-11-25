using Relay.Domain;

namespace Relay.Application;

public interface IMessageRepository
{
    /// <summary>
    /// Saves an accepted message and its outbox row in one transaction. If the
    /// tenant already used the message's idempotency key, nothing is written and
    /// the existing message is returned instead.
    /// </summary>
    Task<Message?> AddAcceptedAsync(Message message, CancellationToken cancellationToken);

    Task<Message?> FindAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);

    Task<Message?> FindByIdempotencyKeyAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Newest first. <paramref name="before"/> is a message id from the previous page.</summary>
    Task<IReadOnlyList<Message>> ListAsync(Guid tenantId, Guid? before, int limit, CancellationToken cancellationToken);
}
