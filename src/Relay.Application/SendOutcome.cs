using Relay.Domain;

namespace Relay.Application;

public abstract record SendOutcome;

public sealed record SendAccepted(IReadOnlyList<Message> Messages, bool Replayed) : SendOutcome
{
    public Message Message => Messages[0];
}

public sealed record SendInvalid(IDictionary<string, string[]> Errors) : SendOutcome;

/// <summary>The idempotency key was already used for a different request.</summary>
public sealed record SendIdempotencyConflict : SendOutcome;

/// <summary>Some recipients are on the tenant's suppression list; nothing was accepted.</summary>
public sealed record SendSuppressed(IReadOnlyList<string> Addresses) : SendOutcome;

public sealed record SendQuotaExceeded(TimeSpan RetryAfter) : SendOutcome;

public sealed record SendPaused(string Reason) : SendOutcome;

/// <summary>Extra context for sends that come through the Agent Send Guard.</summary>
public sealed record SendContext(Guid AgentRunId, string ContentHash, string? HoldReason);
