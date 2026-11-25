namespace Relay.Domain;

public enum MessageStatus
{
    Accepted = 0,
    Held = 1,
    Sending = 2,
    Sent = 3,
    Delivered = 4,
    Bounced = 5,
    Unknown = 6,
    DeadLettered = 7,
    Rejected = 8,
}
