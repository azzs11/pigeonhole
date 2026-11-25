using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Relay.Application;
using Shouldly;
using static Relay.IntegrationTests.RegressionTests;

namespace Relay.IntegrationTests;

[Collection(RelayTestGroup.Name)]
public class MessagesApiTests(RelayFixture fixture)
{
    private static HttpRequestMessage Post(object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    [Fact]
    public async Task Send_is_accepted_with_location_and_a_timeline()
    {
        var tenant = await fixture.CreateTenantAsync();

        var response = await tenant.Http.PostAsJsonAsync("/v1/messages", NewMessage());

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var id = (await response.Content.ReadFromJsonAsync<IdOnly>())!.Id;
        response.Headers.Location!.ToString().ShouldBe($"/v1/messages/{id}");
        using var details = JsonDocument.Parse(await tenant.Http.GetStringAsync($"/v1/messages/{id}"));
        details.RootElement.GetProperty("message").GetProperty("status").GetString().ShouldBe("Accepted");
        details.RootElement.GetProperty("events").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Same_idempotency_key_twice_returns_the_same_message()
    {
        var tenant = await fixture.CreateTenantAsync();

        using var firstRequest = Post(NewMessage(), "order-42");
        using var secondRequest = Post(NewMessage(), "order-42");
        var first = await tenant.Http.SendAsync(firstRequest);
        var second = await tenant.Http.SendAsync(secondRequest);

        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await second.Content.ReadFromJsonAsync<IdOnly>())!.Id
            .ShouldBe((await first.Content.ReadFromJsonAsync<IdOnly>())!.Id);
        second.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        await fixture.WithDbAsync(async db =>
            (await db.Messages.CountAsync(m => m.TenantId == tenant.TenantId)).ShouldBe(1));
    }

    [Fact]
    public async Task Concurrent_requests_with_one_key_create_one_message()
    {
        var tenant = await fixture.CreateTenantAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => tenant.Http.SendAsync(Post(NewMessage(), "race"))));
        var ids = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<IdOnly>()));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Accepted);
        ids.Select(i => i!.Id).Distinct().ShouldHaveSingleItem();
        await fixture.WithDbAsync(async db =>
        {
            (await db.Messages.CountAsync(m => m.TenantId == tenant.TenantId)).ShouldBe(1);
            (await db.Outbox.CountAsync(o => o.TenantId == tenant.TenantId)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_is_422()
    {
        var tenant = await fixture.CreateTenantAsync();
        using var a = Post(NewMessage("A"), "k");
        using var b = Post(NewMessage("B"), "k");
        await tenant.Http.SendAsync(a);

        (await tenant.Http.SendAsync(b)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Header_injection_is_rejected_with_validation_problem()
    {
        var tenant = await fixture.CreateTenantAsync();

        var response = await tenant.Http.PostAsJsonAsync("/v1/messages", NewMessage("Hi\r\nBcc: victim@example.test"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("\"subject\"");
    }

    [Fact]
    public async Task Malformed_json_is_400_problem_details()
    {
        var tenant = await fixture.CreateTenantAsync();
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        var response = await tenant.Http.PostAsync("/v1/messages", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Read_only_key_cannot_send()
    {
        var tenant = await fixture.CreateTenantAsync(Scopes.MessagesRead);

        (await tenant.Http.PostAsJsonAsync("/v1/messages", NewMessage())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_pages_newest_first()
    {
        var tenant = await fixture.CreateTenantAsync();
        for (var i = 0; i < 3; i++)
        {
            await tenant.Http.PostAsJsonAsync("/v1/messages", NewMessage($"m{i}"));
        }

        using var page1 = JsonDocument.Parse(await tenant.Http.GetStringAsync("/v1/messages?limit=2"));
        var next = page1.RootElement.GetProperty("nextBefore").GetString();
        using var page2 = JsonDocument.Parse(await tenant.Http.GetStringAsync($"/v1/messages?limit=2&before={next}"));

        page1.RootElement.GetProperty("items")[0].GetProperty("subject").GetString().ShouldBe("m2");
        page2.RootElement.GetProperty("items").GetArrayLength().ShouldBe(1);
        page2.RootElement.GetProperty("items")[0].GetProperty("subject").GetString().ShouldBe("m0");
    }
}
