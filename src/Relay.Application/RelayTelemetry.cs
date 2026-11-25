using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Relay.Application;

/// <summary>
/// The service's one ActivitySource and Meter, named "Relay". Instruments are created once and
/// never mutated; values flow through them. Exported by OpenTelemetry when configured.
/// </summary>
public static class RelayTelemetry
{
    public const string Name = "Relay";

    public static ActivitySource Source { get; } = new(Name);
    public static Meter Meter { get; } = new(Name);

    public static Counter<long> MessagesAccepted { get; } =
        Meter.CreateCounter<long>("relay.messages.accepted", "{message}", "Messages accepted by the API.");

    public static Counter<long> Transitions { get; } =
        Meter.CreateCounter<long>("relay.messages.transitions", "{transition}", "Delivery state changes, by target status.");

    public static Counter<long> ProviderSends { get; } =
        Meter.CreateCounter<long>("relay.provider.sends", "{attempt}", "Provider send attempts, by provider and outcome.");

    public static Histogram<double> ProviderSendDuration { get; } =
        Meter.CreateHistogram<double>("relay.provider.send.duration", "ms", "Time spent in one provider send, including retries.");

    public static Histogram<double> ClaimDuration { get; } =
        Meter.CreateHistogram<double>("relay.outbox.claim.duration", "ms", "Time to claim one outbox batch.");

    public static Counter<long> Claimed { get; } =
        Meter.CreateCounter<long>("relay.outbox.claimed", "{row}", "Outbox rows claimed by workers.");

    public static Counter<long> WebhookEvents { get; } =
        Meter.CreateCounter<long>("relay.webhook.events", "{event}", "Provider events, by provider and result.");
}
