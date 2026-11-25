using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Relay.Application;
using Relay.Domain;
using Relay.Infrastructure;
using Relay.Infrastructure.Delivery;
using Relay.Infrastructure.Sandbox;
using Shouldly;

namespace Relay.IntegrationTests;

[Collection(RelayTestGroup.Name)]
public class DashboardApiTests(RelayFixture fixture)
{
    [Fact]
    public async Task Stats_count_the_tenants_messages_by_state_and_quota()
    {
        var tenant = await fixture.CreateTenantAsync();
        await tenant.Http.PostAsJsonAsync("/v1/messages", new { to = new[] { "a@example.test", "b@example.test" }, subject = "s", text = "t" });

        using var stats = JsonDocument.Parse(await tenant.Http.GetStringAsync("/v1/stats"));

        stats.RootElement.GetProperty("byStatus").GetProperty("Accepted").GetInt32().ShouldBe(1);
        stats.RootElement.GetProperty("outboxDepth").GetInt32().ShouldBe(1);
        stats.RootElement.GetProperty("quotaUsed").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Sandbox_endpoints_exist_only_when_enabled()
    {
        var tenant = await fixture.CreateTenantAsync();
        (await tenant.Http.GetStringAsync("/v1/sandbox")).ShouldContain("\"enabled\":false");
        (await tenant.Http.PostAsJsonAsync("/v1/sandbox/batch", new { count = 5 })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sandbox_batch_and_simulated_bounce_produce_a_suppression()
    {
        var tenant = await fixture.CreateTenantAsync();
        await using var sandbox = new RelayApiFactory(fixture.ConnectionString, new Dictionary<string, string?> { ["Relay:Sandbox:Enabled"] = "true" });
        var client = sandbox.CreateClient();
        client.DefaultRequestHeaders.Authorization = tenant.Http.DefaultRequestHeaders.Authorization;

        (await client.PostAsJsonAsync("/v1/sandbox/batch", new { count = 3 })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Guid id = default;
        await fixture.WithDbAsync(async db =>
        {
            var m = await db.Messages.Include(x => x.Events).FirstAsync(x => x.TenantId == tenant.TenantId);
            m.Transition(MessageStatus.Sending, DateTimeOffset.UtcNow);
            m.Transition(MessageStatus.Sent, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            id = m.Id;
        });

        (await client.PostAsync(new Uri($"/v1/sandbox/messages/{id}/bounce", UriKind.Relative), null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await tenant.Http.GetStringAsync("/v1/suppressions")).ShouldContain("@sandbox.test");
        (await client.GetStringAsync("/v1/sandbox/promises")).ShouldContain("Nothing sent twice");
    }

    [Fact]
    public async Task Sandbox_switches_take_a_provider_down_and_the_chain_fails_over()
    {
        var primary = new ScriptedProvider("primary", false, ProviderOutcome.Sent("p"));
        var backup = new ScriptedProvider("backup", false, ProviderOutcome.Sent("b"));
        await using var h = await DeliveryHarness.CreateAsync(fixture, new DeliveryOptions { ProviderRetries = 0 }, s =>
        {
            s.AddSingleton<IEmailProvider>(primary);
            s.AddSingleton<IEmailProvider>(backup);
            s.AddRelayOps(new SandboxOptions { Enabled = true });
        }, fakeTime: false);
        await h.WithDbAsync(async db =>
        {
            db.SandboxSwitches.Add(new Relay.Infrastructure.Persistence.SandboxSwitches { DownProviders = ["primary"] });
            await db.SaveChangesAsync();
        });

        var id = await h.AcceptAsync();
        await h.Engine.RunOnceAsync(default);

        (await h.LoadAsync(id)).Message.ProviderName.ShouldBe("backup");
        primary.Calls.ShouldBe(0, "a switched-off provider is never called");
        var snapshot = h.Services.GetRequiredService<ProviderChain>().Snapshot();
        snapshot.Single(p => p.Name == "primary").Failures.ShouldBe(1);
        snapshot.Single(p => p.Name == "backup").Attempts.ShouldBe(1);
    }
}

[Collection(RelayTestGroup.Name)]
public class OpenApiTests(RelayFixture fixture)
{
    [Fact]
    public async Task OpenApi_document_is_served_and_describes_the_dashboard_endpoints()
    {
        var response = await fixture.ClientWithKey(null).GetAsync("/openapi/v1.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var spec = await response.Content.ReadAsStringAsync();
        foreach (var path in new[] { "/v1/messages", "/v1/approvals", "/v1/stats", "/v1/providers", "/v1/agent/messages" })
        {
            spec.ShouldContain($"\"{path}\"");
        }
    }
}
