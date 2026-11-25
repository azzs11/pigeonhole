namespace Relay.Guard;

/// <summary>Which checks run. AgentMailBench compares configurations by switching these.</summary>
public sealed record GuardChecks
{
    public bool Caps { get; init; } = true;
    public bool Provenance { get; init; } = true;
    public bool SecretScan { get; init; } = true;
    public bool Duplicates { get; init; } = true;

    public static GuardChecks None { get; } = new() { Caps = false, Provenance = false, SecretScan = false, Duplicates = false };
    public static GuardChecks CapsOnly { get; } = None with { Caps = true };
    public static GuardChecks CapsAndProvenance { get; } = CapsOnly with { Provenance = true };
    public static GuardChecks Full { get; } = new();
}

/// <summary>The send envelope for one agent run. Set by a human or app when the run is created.</summary>
public sealed record GuardPolicy
{
    /// <summary>Recipients at these domains count as trusted even if not listed on the run.</summary>
    public IReadOnlyList<string> AllowedDomains { get; init; } = [];

    public int MaxRecipientsPerRun { get; init; } = 25;
    public int MaxRecipientsPerMessage { get; init; } = 10;
    public int MaxSendsPerHour { get; init; } = 30;
    public int MaxSendsPerRecipientPerDay { get; init; } = 3;
    public int DuplicateWindowHours { get; init; } = 24;

    /// <summary>Risk at or above this waits for a human; at or above <see cref="BlockThreshold"/> it is refused.</summary>
    public int HoldThreshold { get; init; } = 40;
    public int BlockThreshold { get; init; } = 80;
    public int ApprovalTtlMinutes { get; init; } = 60;

    public GuardChecks Checks { get; init; } = GuardChecks.Full;
}
