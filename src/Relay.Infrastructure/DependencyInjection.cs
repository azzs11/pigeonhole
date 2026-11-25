using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Relay.Application;
using Relay.Infrastructure.Delivery;
using Relay.Infrastructure.Persistence;
using Relay.Infrastructure.Webhooks;

namespace Relay.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers persistence and the Application use cases. Shared by the API, worker and CLI.</summary>
    public static IServiceCollection AddRelayCore(
        this IServiceCollection services, string connectionString, MessagingOptions messaging)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(messaging);
        if (!EmailAddress.IsValid(messaging.DefaultFrom))
        {
            throw new InvalidOperationException($"{MessagingOptions.Section}:DefaultFrom is not a valid email address.");
        }

        services.AddDbContext<RelayDbContext>(o => o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(messaging);
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IApiKeyStore, ApiKeyStore>();
        services.AddScoped<ITenantStore, TenantStore>();
        services.AddScoped<ISuppressionStore, SuppressionStore>();
        services.AddScoped<SendMessageHandler>();
        services.AddScoped<ApiKeyService>();
        services.AddScoped<Relay.Guard.IGuardStore, GuardStore>();
        services.AddScoped<Relay.Guard.AgentGuardService>();
        return services;
    }

    /// <summary>Delivery engine and provider chain. Providers are registered separately, in failover order.</summary>
    public static IServiceCollection AddRelayDelivery(this IServiceCollection services, DeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton(sp => new ProviderChain(
            sp.GetServices<IEmailProvider>(), options, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<DeliveryEngine>();
        return services;
    }

    public static IServiceCollection AddRelayProviders(this IServiceCollection services, IEnumerable<ProviderSettings> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        services.AddHttpClient(nameof(ResendEmailProvider), c => c.Timeout = Timeout.InfiniteTimeSpan);
        foreach (var p in providers)
        {
            services.AddSingleton<IEmailProvider>(sp => p.Type switch
            {
                ProviderType.Smtp => new SmtpEmailProvider(p.Name, p.Smtp ?? new SmtpSettings(), sp.GetRequiredService<TimeProvider>()),
                ProviderType.Resend => new ResendEmailProvider(p.Name, p.Resend ?? throw Missing(p), sp.GetRequiredService<IHttpClientFactory>()),
                ProviderType.Ses => new SesEmailProvider(p.Name, SesEmailProvider.CreateClient(p.Ses ?? throw Missing(p)), sp.GetRequiredService<TimeProvider>()),
                _ => throw new InvalidOperationException($"Unknown provider type {p.Type}."),
            });
        }

        return services;
    }

    /// <summary>Provider webhooks (Resend/Svix, SES/SNS) and the reputation guard.</summary>
    public static IServiceCollection AddRelayWebhooks(
        this IServiceCollection services, WebhookOptions webhooks, ReputationOptions reputation)
    {
        services.AddSingleton(webhooks);
        services.AddSingleton(reputation);
        services.AddHttpClient(nameof(HttpSnsGateway));
        services.TryAddSingleton<ISnsGateway, HttpSnsGateway>();
        services.AddScoped<ReputationGuard>();
        services.AddScoped<DeliveryEventProcessor>();
        return services;
    }

    /// <summary>Sampled gauges (messages by state, outbox depth). Refreshed by the worker.</summary>
    public static IServiceCollection AddRelayQueueMetrics(this IServiceCollection services)
    {
        services.TryAddSingleton<Observability.QueueMetrics>();
        return services;
    }

    private static InvalidOperationException Missing(ProviderSettings p) =>
        new($"Provider '{p.Name}' of type {p.Type} has no {p.Type} settings.");
}
