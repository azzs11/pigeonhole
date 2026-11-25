namespace Relay.Application;

public static class Scopes
{
    public const string MessagesSend = "messages:send";
    public const string MessagesRead = "messages:read";

    public static IReadOnlyList<string> All { get; } = [MessagesSend, MessagesRead];
}
