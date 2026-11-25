using Relay.Domain;

namespace Relay.Application;

/// <summary>What a provider is asked to send. Built from a <see cref="Message"/>.</summary>
public sealed record OutgoingEmail(
    Guid MessageId,
    string InternetMessageId,
    string From,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string? TextBody,
    string? HtmlBody)
{
    public static OutgoingEmail Create(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(message.Id, message.InternetMessageId, message.From, message.To, message.Cc, message.Bcc,
            message.Subject, message.TextBody, message.HtmlBody);
    }
}

public enum ProviderOutcomeKind
{
    /// <summary>The provider accepted the message.</summary>
    Sent,

    /// <summary>Definitely not sent and retrying won't help (bad address, policy rejection).</summary>
    Rejected,

    /// <summary>Definitely not sent; safe to retry or fail over (connection refused, 429, 503).</summary>
    Transient,

    /// <summary>May or may not have been sent (timeout or connection lost after the data was handed over).</summary>
    Unknown,
}

public sealed record ProviderOutcome(ProviderOutcomeKind Kind, string? ProviderMessageId, string Detail)
{
    public static ProviderOutcome Sent(string? providerMessageId) => new(ProviderOutcomeKind.Sent, providerMessageId, "Accepted by provider");

    public static ProviderOutcome Rejected(string detail) => new(ProviderOutcomeKind.Rejected, null, detail);

    public static ProviderOutcome Transient(string detail) => new(ProviderOutcomeKind.Transient, null, detail);

    public static ProviderOutcome Unknown(string detail) => new(ProviderOutcomeKind.Unknown, null, detail);
}

/// <summary>
/// A delivery provider. Implementations never throw for delivery failures: they classify
/// them into a <see cref="ProviderOutcome"/>. Details must not contain addresses or bodies.
/// </summary>
public interface IEmailProvider
{
    string Name { get; }

    /// <summary>True if resending the same message is deduplicated by the provider (e.g. an idempotency key).</summary>
    bool SupportsIdempotentRetry { get; }

    Task<ProviderOutcome> SendAsync(OutgoingEmail email, CancellationToken cancellationToken);
}

public enum UnknownOutcomePolicy
{
    /// <summary>Leave the message in Unknown until a provider event or an operator resolves it.</summary>
    Hold,

    /// <summary>Wait the reconcile window, then send again (may duplicate if the first attempt landed).</summary>
    RetryAfterWindow,

    /// <summary>
    /// Treat unknown as failed and resend at once. This is "an outbox without an Unknown state";
    /// it exists as the MailChaos baseline and duplicates whenever the first attempt landed.
    /// </summary>
    RetryImmediately,
}

public sealed record DeliveryOptions
{
    public const string Section = "Relay:Delivery";

    public int BatchSize { get; init; } = 50;
    public int LeaseSeconds { get; init; } = 120;
    public int PollIntervalMilliseconds { get; init; } = 1000;
    public int MaxAttempts { get; init; } = 6;
    public int BackoffBaseSeconds { get; init; } = 30;
    public int BackoffMaxSeconds { get; init; } = 3600;
    public UnknownOutcomePolicy UnknownOutcome { get; init; } = UnknownOutcomePolicy.Hold;
    public int ReconcileWindowSeconds { get; init; } = 900;

    /// <summary>Per provider call: timeout, in-call retries for transient errors, and the delay between them.</summary>
    public int ProviderTimeoutSeconds { get; init; } = 30;
    public int ProviderRetries { get; init; } = 2;
    public int ProviderRetryDelayMilliseconds { get; init; } = 200;
}

public static class Backoff
{
    /// <summary>Exponential backoff with ±20% jitter: base * 2^(attempt-1), capped.</summary>
    public static TimeSpan ForAttempt(int attempt, DeliveryOptions options, double jitter01)
    {
        ArgumentNullException.ThrowIfNull(options);
        var exponent = Math.Clamp(attempt - 1, 0, 20);
        var seconds = Math.Min(options.BackoffBaseSeconds * Math.Pow(2, exponent), options.BackoffMaxSeconds);
        return TimeSpan.FromSeconds(seconds * (0.8 + (0.4 * Math.Clamp(jitter01, 0, 1))));
    }
}
