namespace Relay.Domain;

/// <summary>One entry in a message's timeline. Appended only by <see cref="Message"/>.</summary>
public sealed class MessageEvent
{
    private MessageEvent()
    {
    }

    internal MessageEvent(Guid messageId, int sequence, MessageStatus? from, MessageStatus to, DateTimeOffset occurredAt, string? detail)
    {
        Id = Guid.CreateVersion7(occurredAt);
        MessageId = messageId;
        Sequence = sequence;
        FromStatus = from;
        ToStatus = to;
        OccurredAt = occurredAt;
        Detail = detail;
    }

    public Guid Id { get; private set; }
    public Guid MessageId { get; private set; }
    public int Sequence { get; private set; }
    public MessageStatus? FromStatus { get; private set; }
    public MessageStatus ToStatus { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string? Detail { get; private set; }
}
