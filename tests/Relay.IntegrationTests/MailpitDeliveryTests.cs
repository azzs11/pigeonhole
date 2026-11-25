using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Relay.Application;
using Relay.Domain;
using Relay.Infrastructure;
using Relay.Infrastructure.Delivery;
using Shouldly;

namespace Relay.IntegrationTests;

/// <summary>Real SMTP into a Mailpit sink. Nothing leaves the container.</summary>
[Collection(RelayTestGroup.Name)]
public class MailpitDeliveryTests(RelayFixture fixture)
{
    private static readonly DeliveryOptions Options = new() { ProviderRetries = 1, ProviderRetryDelayMilliseconds = 0 };

    private ProviderSettings Mailpit(string name = "mailpit") => new()
    {
        Name = name,
        Type = ProviderType.Smtp,
        Smtp = new SmtpSettings { Host = fixture.MailpitHost, Port = fixture.MailpitSmtpPort, Security = SmtpSecurity.None },
    };

    private async Task<JsonElement> FindInMailpitAsync(string subject)
    {
        using var http = new HttpClient { BaseAddress = fixture.MailpitApi };
        using var doc = JsonDocument.Parse(await http.GetStringAsync(
            $"api/v1/search?query={Uri.EscapeDataString($"subject:\"{subject}\"")}"));
        return doc.RootElement.GetProperty("messages").Clone();
    }

    [Fact]
    public async Task Message_posted_to_the_api_arrives_in_mailpit_through_the_worker()
    {
        await using var h = await DeliveryHarness.CreateAsync(
            fixture, Options, s => s.AddRelayProviders([Mailpit()]), fakeTime: false);
        await using var api = new RelayApiFactory(h.ConnectionString);
        string key;
        await using (var scope = h.Services.CreateAsyncScope())
        {
            key = (await scope.ServiceProvider.GetRequiredService<ApiKeyService>()
                .IssueAsync("e2e", "k", Scopes.All, default)).Plaintext;
        }

        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        var subject = $"e2e-{Guid.NewGuid():N}";
        var post = await client.PostAsJsonAsync("/v1/messages",
            new { to = new[] { "someone@example.test" }, subject, text = "Hello", html = "<p>Hello</p>" });
        post.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var id = (await post.Content.ReadFromJsonAsync<RegressionTests.IdOnly>())!.Id;

        await h.Engine.RunOnceAsync(default);

        var (message, _) = await h.LoadAsync(id);
        message.Status.ShouldBe(MessageStatus.Sent);
        var found = (await FindInMailpitAsync(subject)).EnumerateArray().ShouldHaveSingleItem();
        found.GetProperty("MessageID").GetString().ShouldBe(message.InternetMessageId.Trim('<', '>'));
        found.GetProperty("To")[0].GetProperty("Address").GetString().ShouldBe("someone@example.test");
    }

    [Fact]
    public async Task Smtp_server_down_fails_over_to_the_next_one()
    {
        var dead = Mailpit("dead") with
        {
            Smtp = new SmtpSettings { Host = "127.0.0.1", Port = 1, Security = SmtpSecurity.None },
        };
        await using var h = await DeliveryHarness.CreateAsync(
            fixture, Options, s => s.AddRelayProviders([dead, Mailpit()]), fakeTime: false);
        var subject = $"failover-{Guid.NewGuid():N}";
        var id = await h.AcceptAsync(subject);

        await h.Engine.RunOnceAsync(default);

        var (message, _) = await h.LoadAsync(id);
        message.Status.ShouldBe(MessageStatus.Sent);
        message.ProviderName.ShouldBe("mailpit");
        (await FindInMailpitAsync(subject)).GetArrayLength().ShouldBe(1);
    }
}
