using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Relay.Application;
using Relay.Domain;
using Relay.Infrastructure;
using Relay.Infrastructure.Delivery;
using Relay.Infrastructure.Persistence;

namespace Relay.IntegrationTests;

/// <summary>A delivery engine on its own fresh database, so outbox rows from other tests never leak in.</summary>
public sealed class DeliveryHarness : IAsyncDisposable
{
    private DeliveryHarness(ServiceProvider services, string connectionString, FakeTimeProvider? time)
    {
        Services = services;
        ConnectionString = connectionString;
        Time = time;
        Engine = services.GetRequiredService<DeliveryEngine>();
    }

    public ServiceProvider Services { get; }
    public string ConnectionString { get; }
    public FakeTimeProvider? Time { get; }
    public DeliveryEngine Engine { get; }

    public static async Task<DeliveryHarness> CreateAsync(
        RelayFixture fixture, DeliveryOptions options, Action<IServiceCollection> addProviders, bool fakeTime = true)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(addProviders);
        var connectionString = await fixture.CreateFreshDatabaseAsync();
        var services = new ServiceCollection().AddLogging();
        FakeTimeProvider? time = null;
        if (fakeTime)
        {
            time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
            services.AddSingleton<TimeProvider>(time);
        }

        services.AddRelayCore(connectionString, new MessagingOptions()).AddRelayDelivery(options);
        addProviders(services);
        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RelayDbContext>().Database.MigrateAsync();
        }

        return new DeliveryHarness(provider, connectionString, time);
    }

    public async Task<Guid> AcceptAsync(string subject = "Hello")
    {
        await using var scope = Services.CreateAsyncScope();
        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
        var tenant = (await keys.IssueAsync("delivery", "k", Scopes.All, default)).Key.TenantId;
        var outcome = await scope.ServiceProvider.GetRequiredService<SendMessageHandler>().HandleAsync(
            tenant, new SendMessageRequest { To = ["to@example.test"], Subject = subject, Text = "Body" }, null, default);
        return ((SendAccepted)outcome).Message.Id;
    }

    public async Task<(Message Message, OutboxMessage Outbox)> LoadAsync(Guid messageId)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var message = await db.Messages.AsNoTracking().Include(m => m.Events).SingleAsync(m => m.Id == messageId);
        var outbox = await db.Outbox.AsNoTracking().SingleAsync(o => o.MessageId == messageId);
        return (message, outbox);
    }

    public async Task WithDbAsync(Func<RelayDbContext, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<RelayDbContext>());
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}

/// <summary>Returns scripted outcomes in order (repeating the last one) and counts calls.</summary>
public sealed class ScriptedProvider(string name, bool idempotent, params ProviderOutcome[] script) : IEmailProvider
{
    private int _calls;

    public string Name => name;
    public bool SupportsIdempotentRetry => idempotent;
    public int Calls => _calls;

    public Task<ProviderOutcome> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        return Task.FromResult(script[Math.Min(call, script.Length) - 1]);
    }
}

/// <summary>Never answers: the chain's timeout fires and the outcome is Unknown.</summary>
public sealed class HangingProvider(string name) : IEmailProvider
{
    public string Name => name;
    public bool SupportsIdempotentRetry => false;

    public async Task<ProviderOutcome> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return ProviderOutcome.Sent(null);
    }
}
