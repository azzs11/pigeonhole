namespace Relay.Application;

public sealed record SendMessageRequest
{
    public string? From { get; init; }
    public IReadOnlyList<string>? To { get; init; }
    public IReadOnlyList<string>? Cc { get; init; }
    public IReadOnlyList<string>? Bcc { get; init; }
    public string? Subject { get; init; }
    public string? Text { get; init; }
    public string? Html { get; init; }
}

/// <summary>Same content to many recipients: one message (and one quota unit) per recipient.</summary>
public sealed record SendBulkRequest
{
    public string? From { get; init; }
    public IReadOnlyList<string>? Recipients { get; init; }
    public string? Subject { get; init; }
    public string? Text { get; init; }
    public string? Html { get; init; }
}
