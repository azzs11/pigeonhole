namespace Relay.Infrastructure.Persistence;

/// <summary>
/// One row per accepted message, written in the same transaction. The Phase 2 relay
/// claims rows with FOR UPDATE SKIP LOCKED and a lease.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public int Attempts { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Set after an unknown outcome on an idempotent provider: retry only there.</summary>
    public string? PinnedProvider { get; set; }

    /// <summary>W3C traceparent of the request that accepted the message, so the worker continues its trace.</summary>
    public string? TraceParent { get; set; }
}
