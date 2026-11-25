using Relay.Domain;

namespace Relay.Application;

public sealed record ExplainedStep(DateTimeOffset At, MessageStatus? From, MessageStatus To, string Explanation);

public sealed record Explanation(MessageStatus Status, string Summary, IReadOnlyList<ExplainedStep> Steps);

/// <summary>Plain-language reasons for every state change, for people and for agents.</summary>
public static class MessageExplainer
{
    public static Explanation Explain(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var steps = message.Events
            .OrderBy(e => e.Sequence)
            .Select(e => new ExplainedStep(e.OccurredAt, e.FromStatus, e.ToStatus, Step(e.FromStatus, e.ToStatus, e.Detail)))
            .ToList();
        return new Explanation(message.Status, Summary(message), steps);
    }

    private static string Step(MessageStatus? from, MessageStatus to, string? detail)
    {
        var why = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";
        return (from, to) switch
        {
            (null, MessageStatus.Accepted) => "Accepted and stored together with its outbox entry, so it cannot be lost. Waiting for a worker.",
            _ when from == to => $"Note{why}.",
            (MessageStatus.Accepted, MessageStatus.Sending) => $"A worker claimed it and is handing it to a provider{why}.",
            (MessageStatus.Sending, MessageStatus.Sent) => $"The provider accepted it{why}.",
            (MessageStatus.Sending, MessageStatus.Accepted) => $"The attempt failed before any provider took it, so it is safe to retry; it is queued again with backoff{why}.",
            (MessageStatus.Sending, MessageStatus.Unknown) => $"We cannot tell whether the provider received it{why}. To avoid sending twice it is not resent blindly; it waits for a provider event or the reconcile policy.",
            (MessageStatus.Unknown, MessageStatus.Sent) => $"A provider event confirmed it had been sent, so no resend is needed{why}.",
            (MessageStatus.Unknown, MessageStatus.Accepted) => $"Retrying after an unknown outcome{why}.",
            (MessageStatus.Sent, MessageStatus.Delivered) => $"The recipient's mail server accepted it{why}.",
            (MessageStatus.Sent, MessageStatus.Bounced) => $"It bounced{why}. A hard bounce adds the address to the suppression list.",
            (MessageStatus.Sent, MessageStatus.Accepted) => $"A temporary (soft) bounce; it will be retried with backoff{why}.",
            (_, MessageStatus.DeadLettered) => $"Delivery was abandoned{why}. It needs an operator to look at it.",
            (MessageStatus.Accepted, MessageStatus.Rejected) => $"Not sent{why}.",
            (MessageStatus.Accepted, MessageStatus.Held) => $"Held for human approval{why}.",
            (MessageStatus.Held, MessageStatus.Accepted) => $"Approved and queued for sending{why}.",
            (MessageStatus.Held, MessageStatus.Rejected) => $"Rejected by a reviewer{why}.",
            _ => $"Moved from {from} to {to}{why}.",
        };
    }

    private static string Summary(Message m)
    {
        var attempts = m.Events.Count(e => e.ToStatus == MessageStatus.Sending);
        var tries = attempts == 1 ? "1 attempt" : $"{attempts} attempts";
        return m.Status switch
        {
            MessageStatus.Accepted when attempts == 0 => "Queued; no delivery attempt yet.",
            MessageStatus.Accepted => $"Queued for retry after {tries}.",
            MessageStatus.Sending => "A worker is sending it right now.",
            MessageStatus.Sent => $"Handed to {m.ProviderName} after {tries}; waiting for delivery confirmation.",
            MessageStatus.Delivered => $"Delivered via {m.ProviderName} after {tries}.",
            MessageStatus.Bounced => "Bounced; see the last step for the reason.",
            MessageStatus.Unknown => "Outcome unknown; held to avoid a duplicate until a provider event or the reconcile policy resolves it.",
            MessageStatus.DeadLettered => $"Abandoned after {tries}; needs an operator.",
            MessageStatus.Rejected => "Not sent; see the last step for the reason.",
            MessageStatus.Held => "Waiting for human approval.",
            _ => m.Status.ToString(),
        };
    }
}
