namespace Relay.Application;

public sealed record MessagingOptions
{
    public const string Section = "Relay:Messaging";

    /// <summary>Sender used when a request has no "from". Must be a valid address.</summary>
    public string DefaultFrom { get; init; } = "noreply@relay.test";
    public int MaxRecipients { get; init; } = 50;
    public int MaxSubjectLength { get; init; } = 998;
    public int MaxBodyBytes { get; init; } = 512 * 1024;
    public int MaxIdempotencyKeyLength { get; init; } = 255;

    /// <summary>Right-hand side of generated Message-IDs, e.g. the sending domain.</summary>
    public string MessageIdDomain { get; init; } = "relay.test";
}
