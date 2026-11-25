using System.Net.Http.Headers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Relay.Application;
using Relay.Infrastructure.Persistence;
using Relay.Infrastructure.Webhooks;
using Testcontainers.PostgreSql;

namespace Relay.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class RelayTestGroup : ICollectionFixture<RelayFixture>
{
    public const string Name = "relay";
}

public sealed class RelayApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Relay", connectionString);
        builder.UseSetting("Relay:RateLimit:PermitsPerSecond", "100000");
        builder.UseSetting("Relay:RateLimit:Burst", "100000");
        builder.UseSetting("Relay:RateLimit:AnonymousPermitsPerSecond", "100000");
        builder.UseSetting("Relay:RateLimit:AnonymousBurst", "100000");
        builder.UseSetting("Relay:Webhooks:Resend:SigningSecret", TestSigning.ResendSecret);
        builder.UseSetting("Relay:Webhooks:Ses:AllowedTopicArns:0", TestSigning.TopicArn);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(s => s.AddSingleton<ISnsGateway>(TestSigning.Sns));
    }
}

/// <summary>One PostgreSQL container and one migrated database shared by the collection.</summary>
public sealed class RelayFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=500")
        .Build();
    private readonly IContainer _mailpit = new ContainerBuilder("axllent/mailpit:v1.34.0")
        .WithPortBinding(1025, true)
        .WithPortBinding(8025, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/livez")))
        .Build();

    public RelayApiFactory Factory { get; private set; } = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    public string MailpitHost => _mailpit.Hostname;
    public int MailpitSmtpPort => _mailpit.GetMappedPublicPort(1025);
    public Uri MailpitApi => new($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(8025)}/");

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _mailpit.StartAsync());
        Factory = new RelayApiFactory(_postgres.GetConnectionString());
        await WithDbAsync(db => db.Database.MigrateAsync());
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _mailpit.DisposeAsync();
    }

    public async Task WithDbAsync(Func<RelayDbContext, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var scope = Factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<RelayDbContext>());
    }

    /// <summary>A new tenant with its own key, and a client that sends it.</summary>
    public async Task<TenantClient> CreateTenantAsync(params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        await using var scope = Factory.Services.CreateAsyncScope();
        var issued = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().IssueAsync(
            $"tenant-{Guid.NewGuid():N}", "test", scopes.Length == 0 ? Scopes.All : scopes, CancellationToken.None);
        return new TenantClient(issued.Key.TenantId, issued.Plaintext, ClientWithKey(issued.Plaintext));
    }

    public HttpClient ClientWithKey(string? key)
    {
        var client = Factory.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return client;
    }

    /// <summary>A brand-new, migrated database in the same container.</summary>
    public async Task<string> CreateFreshDatabaseAsync()
    {
        var name = $"fresh_{Guid.NewGuid():N}";
        await using (var conn = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
#pragma warning disable CA2100 // name is a generated GUID, not user input
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
#pragma warning restore CA2100
            await cmd.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

public sealed record TenantClient(Guid TenantId, string Key, HttpClient Http);
