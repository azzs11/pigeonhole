namespace Relay.Application;

public sealed record MessagingOptions
{
    public const string Section = "Relay:Messaging";

    /// <summary>Sender used when a request has no "from". Must be a valid address.</summary>
    public string DefaultFrom { get; init; } = "noreply@relay.test";
    public int MaxRecipients { get; init; } = 50;
    public int MaxBulkRecipients { get; init; } = 1000;
    public int MaxSubjectLength { get; init; } = 998;
    public int MaxBodyBytes { get; init; } = 512 * 1024;
    public int MaxIdempotencyKeyLength { get; init; } = 200;

    /// <summary>Right-hand side of generated Message-IDs, e.g. the sending domain.</summary>
    public string MessageIdDomain { get; init; } = "relay.test";

    /// <summary>Daily recipient quota given to new tenants.</summary>
    public int DefaultDailyQuota { get; init; } = 10_000;
}

/// <summary>Auto-pause thresholds. Rates are over the trailing window and need a minimum volume.</summary>
public sealed record ReputationOptions
{
    public const string Section = "Relay:Reputation";

    public bool Enabled { get; init; } = true;
    public double MaxBounceRate { get; init; } = 0.05;
    public double MaxComplaintRate { get; init; } = 0.001;
    public int MinimumVolume { get; init; } = 100;
    public int WindowHours { get; init; } = 24;
}
