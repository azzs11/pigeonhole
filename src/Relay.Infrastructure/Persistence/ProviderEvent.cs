namespace Relay.Infrastructure.Persistence;

/// <summary>
/// One row per provider webhook event, unique on (provider, event id). Inserted in the same
/// transaction as the event's effects, so a replayed or duplicated event changes nothing.
/// </summary>
public sealed class ProviderEvent
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public Guid? MessageId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
