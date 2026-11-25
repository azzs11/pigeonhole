using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Relay.Application;
using Relay.Infrastructure.Persistence;

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
        services.AddScoped<SendMessageHandler>();
        services.AddScoped<ApiKeyService>();
        return services;
    }
}
