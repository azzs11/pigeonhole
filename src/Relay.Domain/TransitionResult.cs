namespace Relay.Domain;

public readonly record struct TransitionResult(bool Succeeded, MessageStatus From, MessageStatus To)
{
    public string? Error => Succeeded ? null : $"Illegal transition {From} -> {To}.";
}
