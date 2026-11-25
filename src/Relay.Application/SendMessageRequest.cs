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
