using Relay.Domain;

namespace Relay.Application;

public abstract record SendOutcome;

public sealed record SendAccepted(Message Message, bool Replayed) : SendOutcome;

public sealed record SendInvalid(IDictionary<string, string[]> Errors) : SendOutcome;

/// <summary>The idempotency key was already used for a different request.</summary>
public sealed record SendIdempotencyConflict : SendOutcome;
