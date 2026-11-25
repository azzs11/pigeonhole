using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Relay.Application;
using Relay.Domain;
using Relay.Infrastructure.Persistence;
using Shouldly;

namespace Relay.IntegrationTests;

/// <summary>One test per High red flag from the plan, so each fix stays fixed.</summary>
[Collection(RelayTestGroup.Name)]
public class RegressionTests(RelayFixture fixture)
{
    internal static object NewMessage(string subject = "Hello") =>
        new { to = new[] { "to@example.test" }, subject, text = "Body" };

    [Fact]
    public async Task Tenant_cannot_read_another_tenants_message()
    {
        var alice = await fixture.CreateTenantAsync();
        var bob = await fixture.CreateTenantAsync();
        var sent = await alice.Http.PostAsJsonAsync("/v1/messages", NewMessage());
        var id = (await sent.Content.ReadFromJsonAsync<IdOnly>())!.Id;

        (await bob.Http.GetAsync($"/v1/messages/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await alice.Http.GetAsync($"/v1/messages/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await bob.Http.GetStringAsync("/v1/messages")).ShouldNotContain(id.ToString());
    }

    [Fact]
    public async Task Fresh_database_has_no_api_keys_and_the_old_seed_key_is_rejected()
    {
        var connectionString = await fixture.CreateFreshDatabaseAsync();
        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options;
        await using (var db = new RelayDbContext(options))
        {
            await db.Database.MigrateAsync();
            (await db.ApiKeys.CountAsync()).ShouldBe(0);
            (await db.Tenants.CountAsync()).ShouldBe(0);
        }

        var old = fixture.ClientWithKey("test-api-key-12345");
        (await old.GetAsync("/v1/messages")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_keys_are_stored_as_hashes_and_verified_by_hash()
    {
        var tenant = await fixture.CreateTenantAsync();
        ApiKey row = null!;
        await fixture.WithDbAsync(async db =>
            row = await db.ApiKeys.AsNoTracking().SingleAsync(k => k.TenantId == tenant.TenantId));

        row.Hash.ShouldBe(ApiKeyFormat.Hash(tenant.Key));
        var secret = tenant.Key[ApiKeyFormat.PrefixLength..];
        string[] stored = [row.Hash, row.Prefix, row.Name, .. row.Scopes];
        stored.ShouldAllBe(v => !v.Contains(secret, StringComparison.Ordinal));

        var tampered = tenant.Key[..^2] + (tenant.Key[^2..] == "AA" ? "BB" : "AA");
        (await fixture.ClientWithKey(tampered).GetAsync("/v1/messages")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await tenant.Http.GetAsync("/v1/messages")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unexpected_errors_return_problem_details_without_exception_text()
    {
        var tenant = await fixture.CreateTenantAsync();
        using var failing = fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddScoped<IMessageRepository, ThrowingRepository>()));
        var client = failing.CreateClient();
        client.DefaultRequestHeaders.Authorization = tenant.Http.DefaultRequestHeaders.Authorization;

        var response = await client.PostAsJsonAsync("/v1/messages", NewMessage());
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        body.ShouldNotContain(ThrowingRepository.Secret);
        body.ShouldNotContain("InvalidOperationException");
        body.ShouldContain("traceId");

        var unauthorized = await fixture.ClientWithKey(null).GetAsync("/v1/messages");
        unauthorized.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await unauthorized.Content.ReadAsStringAsync()).ShouldNotContain("\"detail\"");
    }

    [Fact]
    public async Task Concurrent_sends_store_one_message_and_one_outbox_row_each()
    {
        const int Total = 1000;
        const int Workers = 8;
        var tenant = await fixture.CreateTenantAsync();

        var statuses = await Task.WhenAll(Enumerable.Range(0, Workers).Select(w => Task.Run(async () =>
        {
            var results = new List<HttpStatusCode>();
            for (var i = w; i < Total; i += Workers)
            {
                using var response = await tenant.Http.PostAsJsonAsync("/v1/messages", NewMessage($"m{i}"));
                results.Add(response.StatusCode);
            }

            return results;
        })));

        statuses.SelectMany(s => s).ShouldAllBe(s => s == HttpStatusCode.Accepted);
        await fixture.WithDbAsync(async db =>
        {
            (await db.Messages.CountAsync(m => m.TenantId == tenant.TenantId)).ShouldBe(Total);
            (await db.Outbox.CountAsync(o => o.TenantId == tenant.TenantId)).ShouldBe(Total);
        });
    }

    internal sealed record IdOnly(Guid Id);

    private sealed class ThrowingRepository : IMessageRepository
    {
        public const string Secret = "Password=hunter2";

        public Task<Message?> AddAcceptedAsync(Message message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"connection failed: {Secret}");

        public Task<Message?> FindAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Secret);

        public Task<Message?> FindByIdempotencyKeyAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Secret);

        public Task<IReadOnlyList<Message>> ListAsync(Guid tenantId, Guid? before, int limit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Secret);
    }
}
