using Relay.Domain;

namespace Relay.Application;

public abstract record AddResult;

public sealed record AddSucceeded : AddResult;

/// <summary>The tenant already used this idempotency key; nothing was written.</summary>
public sealed record AddDuplicate(IReadOnlyList<Message> Existing) : AddResult;

public sealed record AddQuotaExceeded(TimeSpan RetryAfter) : AddResult;

public sealed record AddPaused(string Reason) : AddResult;

public interface IMessageRepository
{
    /// <summary>
    /// In one transaction: atomically consumes <paramref name="quotaUnits"/> from the tenant's daily
    /// quota (refused if paused or over quota), then saves the messages and one outbox row each.
    /// </summary>
    Task<AddResult> AddAcceptedAsync(IReadOnlyList<Message> messages, int quotaUnits, CancellationToken cancellationToken);

    Task<Message?> FindAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);

    /// <summary>Messages whose key is exactly <paramref name="idempotencyKey"/> or starts with "{key}#" (bulk).</summary>
    Task<IReadOnlyList<Message>> FindByIdempotencyKeyAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Newest first. <paramref name="before"/> is a message id from the previous page.</summary>
    Task<IReadOnlyList<Message>> ListAsync(Guid tenantId, Guid? before, int limit, CancellationToken cancellationToken);
}

public interface ISuppressionStore
{
    /// <summary>Returns the normalized addresses from <paramref name="addresses"/> that the tenant has suppressed.</summary>
    Task<IReadOnlyList<string>> FindSuppressedAsync(Guid tenantId, IEnumerable<string> addresses, CancellationToken cancellationToken);

    Task<IReadOnlyList<Suppression>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(Guid tenantId, string address, CancellationToken cancellationToken);
}
