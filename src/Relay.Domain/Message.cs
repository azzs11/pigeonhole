namespace Relay.Domain;

/// <summary>
/// A message and its delivery state machine. Every status change goes through
/// <see cref="Transition"/>, which rejects illegal moves and appends an event.
/// </summary>
public sealed class Message
{
    private static readonly Dictionary<MessageStatus, MessageStatus[]> Allowed = new()
    {
        [MessageStatus.Accepted] = [MessageStatus.Sending, MessageStatus.Held],
        [MessageStatus.Held] = [MessageStatus.Accepted, MessageStatus.Rejected],
        [MessageStatus.Sending] = [MessageStatus.Sent, MessageStatus.Unknown, MessageStatus.Accepted, MessageStatus.DeadLettered],
        [MessageStatus.Unknown] = [MessageStatus.Sent, MessageStatus.Accepted, MessageStatus.DeadLettered],
        [MessageStatus.Sent] = [MessageStatus.Delivered, MessageStatus.Bounced],
    };

    private readonly List<MessageEvent> _events = [];

    private Message()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string From { get; private set; } = string.Empty;
    public IReadOnlyList<string> To { get; private set; } = [];
    public IReadOnlyList<string> Cc { get; private set; } = [];
    public IReadOnlyList<string> Bcc { get; private set; } = [];
    public string Subject { get; private set; } = string.Empty;
    public string? TextBody { get; private set; }
    public string? HtmlBody { get; private set; }
    public MessageStatus Status { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public string? RequestHash { get; private set; }
    /// <summary>Deterministic RFC 5322 Message-ID, the same on every send attempt.</summary>
    public string InternetMessageId { get; private set; } = string.Empty;
    public string? ProviderName { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<MessageEvent> Events => _events;

    public static bool IsTerminal(MessageStatus status) => !Allowed.ContainsKey(status);

    public static bool CanTransition(MessageStatus from, MessageStatus to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    public static Message Accept(
        Guid id, Guid tenantId, MessageDraft draft, string? idempotencyKey, string? requestHash, string messageIdDomain, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant is required.", nameof(tenantId));
        }

        if (draft.To.Count == 0)
        {
            throw new ArgumentException("At least one recipient is required.", nameof(draft));
        }

        var message = new Message
        {
            Id = id,
            TenantId = tenantId,
            From = draft.From,
            To = [.. draft.To],
            Cc = [.. draft.Cc],
            Bcc = [.. draft.Bcc],
            Subject = draft.Subject,
            TextBody = draft.TextBody,
            HtmlBody = draft.HtmlBody,
            Status = MessageStatus.Accepted,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            InternetMessageId = $"<{id:N}@{messageIdDomain}>",
            CreatedAt = now,
            UpdatedAt = now,
        };
        message._events.Add(new MessageEvent(id, 1, null, MessageStatus.Accepted, now, "Accepted"));
        return message;
    }

    /// <summary>Records which provider handled the latest attempt (and its id, once known).</summary>
    public void RecordProvider(string providerName, string? providerMessageId)
    {
        ProviderName = providerName;
        ProviderMessageId = providerMessageId;
    }

    public TransitionResult Transition(MessageStatus to, DateTimeOffset at, string? detail = null)
    {
        var from = Status;
        if (!CanTransition(from, to))
        {
            return new TransitionResult(false, from, to);
        }

        Status = to;
        UpdatedAt = at;
        _events.Add(new MessageEvent(Id, _events.Count + 1, from, to, at, detail));
        return new TransitionResult(true, from, to);
    }
}
