namespace Relay.Infrastructure.Webhooks;

public enum DeliveryEventKind
{
    Ignored,
    Sent,
    Delivered,
    HardBounce,
    SoftBounce,
    Complaint,
}

/// <summary>A provider event, normalized. Matched to a message by provider id, else by Message-ID.</summary>
public sealed record DeliveryEvent(
    string Provider,
    string EventId,
    DeliveryEventKind Kind,
    string? ProviderMessageId,
    string? InternetMessageId,
    IReadOnlyList<string> Recipients,
    string Detail);

public sealed record WebhookOptions
{
    public const string Section = "Relay:Webhooks";

    public ResendWebhookOptions Resend { get; init; } = new();
    public SesWebhookOptions Ses { get; init; } = new();
}

public sealed record ResendWebhookOptions
{
    /// <summary>The endpoint's signing secret, "whsec_...". Empty disables the endpoint.</summary>
    public string? SigningSecret { get; init; }
    public int ToleranceSeconds { get; init; } = 300;
}

public sealed record SesWebhookOptions
{
    /// <summary>SNS topics we accept. Empty disables the endpoint.</summary>
    public IReadOnlyList<string> AllowedTopicArns { get; init; } = [];
    public int ToleranceSeconds { get; init; } = 3600;
}
