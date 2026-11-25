using Relay.Application;
using Relay.Infrastructure;
using Relay.Infrastructure.Delivery;
using Relay.Worker;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Relay")
    ?? throw new InvalidOperationException("ConnectionStrings:Relay is not configured.");
var messaging = builder.Configuration.GetSection(MessagingOptions.Section).Get<MessagingOptions>() ?? new MessagingOptions();
var delivery = builder.Configuration.GetSection(DeliveryOptions.Section).Get<DeliveryOptions>() ?? new DeliveryOptions();
var providers = builder.Configuration.GetSection("Relay:Providers").Get<ProviderSettings[]>() ?? [];
if (providers.Length == 0)
{
    throw new InvalidOperationException("Relay:Providers is empty. Configure at least one delivery provider.");
}

builder.Services
    .AddRelayCore(connectionString, messaging)
    .AddRelayDelivery(delivery)
    .AddRelayProviders(providers)
    .AddHostedService<OutboxRelayService>();

using var host = builder.Build();
await host.RunAsync();
