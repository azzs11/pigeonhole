namespace Relay.Domain;

/// <summary>Validated content for a new message. Validation happens in the Application layer.</summary>
public sealed record MessageDraft(
    string From,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string? TextBody,
    string? HtmlBody);
