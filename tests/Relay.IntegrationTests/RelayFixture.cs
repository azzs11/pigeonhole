using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Relay.Application;
using Relay.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Relay.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class RelayTestGroup : ICollectionFixture<RelayFixture>
{
    public const string Name = "relay";
}

public sealed class RelayApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Relay", connectionString);
    }
}

/// <summary>One PostgreSQL container and one migrated database shared by the collection.</summary>
public sealed class RelayFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public RelayApiFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = new RelayApiFactory(_postgres.GetConnectionString());
        await WithDbAsync(db => db.Database.MigrateAsync());
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
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
