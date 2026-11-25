using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Relay.Domain;
using Shouldly;
using static Relay.IntegrationTests.RegressionTests;

namespace Relay.IntegrationTests;

/// <summary>Phase 3: webhooks, suppression, quotas, rate limits and the reputation guard.</summary>
[Collection(RelayTestGroup.Name)]
public class FeedbackLoopTests(RelayFixture fixture)
{
    private static object Msg(string to, string subject = "Hello") => new { to = new[] { to }, subject, text = "Body" };

    private static string Unique(string name) => $"{name}-{Guid.NewGuid():N}@example.test";

    /// <summary>Sends through the API, then records it as handed to a provider (what the worker does).</summary>
    private async Task<Guid> SentAsync(TenantClient tenant, string to, string provider, string providerId, MessageStatus final = MessageStatus.Sent)
    {
        var response = await tenant.Http.PostAsJsonAsync("/v1/messages", Msg(to));
        var id = (await response.Content.ReadFromJsonAsync<IdOnly>())!.Id;
        await fixture.WithDbAsync(async db =>
        {
            var m = await db.Messages.Include(x => x.Events).SingleAsync(x => x.Id == id);
            m.Transition(MessageStatus.Sending, DateTimeOffset.UtcNow);
            m.Transition(final, DateTimeOffset.UtcNow);
            m.RecordProvider(provider, providerId);
            var outbox = await db.Outbox.SingleAsync(o => o.MessageId == id);
            outbox.Attempts = 1;
            outbox.ProcessedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });
        return id;
    }

    private async Task<Message> LoadAsync(Guid id)
    {
        Message m = null!;
        await fixture.WithDbAsync(async db => m = await db.Messages.AsNoTracking().Include(x => x.Events).SingleAsync(x => x.Id == id));
        return m;
    }

    private static object ResendEvent(string type, string emailId, string to, string? bounceType = null) => new
    {
        type,
        created_at = DateTimeOffset.UtcNow,
        data = bounceType is null
            ? (object)new { email_id = emailId, to = new[] { to } }
            : new { email_id = emailId, to = new[] { to }, bounce = new { type = bounceType } },
    };

    [Fact]
    public async Task Replaying_the_same_webhook_changes_nothing()
    {
        var tenant = await fixture.CreateTenantAsync();
        var providerId = $"re_{Guid.NewGuid():N}";
        var id = await SentAsync(tenant, Unique("r"), "resend", providerId);
        var client = fixture.ClientWithKey(null);
        var payload = ResendEvent("email.delivered", providerId, "x@example.test");

        using var first = TestSigning.ResendRequest(payload, "evt-replay-" + providerId);
        (await (await client.SendAsync(first)).Content.ReadAsStringAsync()).ShouldContain("Applied");
        var afterFirst = await LoadAsync(id);

        using var replay = TestSigning.ResendRequest(payload, "evt-replay-" + providerId);
        var second = await client.SendAsync(replay);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Content.ReadAsStringAsync()).ShouldContain("Duplicate");
        var afterReplay = await LoadAsync(id);
        afterReplay.Status.ShouldBe(MessageStatus.Delivered);
        afterReplay.Events.Count.ShouldBe(afterFirst.Events.Count);
        await fixture.WithDbAsync(async db =>
            (await db.ProviderEvents.CountAsync(e => e.EventId == "evt-replay-" + providerId)).ShouldBe(1));
    }

    [Fact]
    public async Task Bad_or_stale_signatures_are_rejected()
    {
        var client = fixture.ClientWithKey(null);
        var payload = ResendEvent("email.delivered", "re_x", "x@example.test");

        using var tampered = TestSigning.ResendRequest(payload, tamper: true);
        (await client.SendAsync(tampered)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var stale = TestSigning.ResendRequest(payload, at: DateTimeOffset.UtcNow.AddMinutes(-30));
        (await client.SendAsync(stale)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var badSes = TestSigning.SesRequest(new { notificationType = "Delivery", mail = new { messageId = "s" } }, tamper: true);
        (await client.SendAsync(badSes)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var wrongTopic = TestSigning.SesRequest(new { notificationType = "Delivery", mail = new { messageId = "s" } }, topic: "arn:aws:sns:us-east-1:1:other");
        (await client.SendAsync(wrongTopic)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Hard_bounce_suppresses_the_address_until_it_is_removed()
    {
        var tenant = await fixture.CreateTenantAsync();
        var address = Unique("gone");
        var providerId = $"re_{Guid.NewGuid():N}";
        var id = await SentAsync(tenant, address, "resend", providerId);

        using var bounce = TestSigning.ResendRequest(ResendEvent("email.bounced", providerId, address, "Permanent"));
        (await fixture.ClientWithKey(null).SendAsync(bounce)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoadAsync(id)).Status.ShouldBe(MessageStatus.Bounced);

        var blocked = await tenant.Http.PostAsJsonAsync("/v1/messages", Msg(address.ToUpperInvariant()));
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await blocked.Content.ReadAsStringAsync()).ShouldContain(address);

        (await tenant.Http.GetStringAsync("/v1/suppressions")).ShouldContain("HardBounce");
        (await tenant.Http.DeleteAsync($"/v1/suppressions/{Uri.EscapeDataString(address)}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await tenant.Http.PostAsJsonAsync("/v1/messages", Msg(address))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Soft_bounce_requeues_the_message_with_backoff()
    {
        var tenant = await fixture.CreateTenantAsync();
        var providerId = $"re_{Guid.NewGuid():N}";
        var id = await SentAsync(tenant, Unique("full"), "resend", providerId);

        using var soft = TestSigning.ResendRequest(ResendEvent("email.bounced", providerId, "full@example.test", "Transient"));
        await fixture.ClientWithKey(null).SendAsync(soft);

        (await LoadAsync(id)).Status.ShouldBe(MessageStatus.Accepted);
        await fixture.WithDbAsync(async db =>
        {
            var outbox = await db.Outbox.SingleAsync(o => o.MessageId == id);
            outbox.ProcessedAt.ShouldBeNull();
            outbox.AvailableAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
            (await db.Suppressions.CountAsync(s => s.TenantId == tenant.TenantId)).ShouldBe(0);
        });
    }

    [Fact]
    public async Task Ses_events_reconcile_an_unknown_message_by_message_id()
    {
        var tenant = await fixture.CreateTenantAsync();
        var id = await SentAsync(tenant, Unique("u"), "ses", providerId: null!, final: MessageStatus.Unknown);
        var internetId = (await LoadAsync(id)).InternetMessageId;

        using var delivery = TestSigning.SesRequest(new
        {
            notificationType = "Delivery",
            mail = new { messageId = $"ses-{Guid.NewGuid():N}", commonHeaders = new { messageId = internetId } },
        });
        (await fixture.ClientWithKey(null).SendAsync(delivery)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var m = await LoadAsync(id);
        m.Status.ShouldBe(MessageStatus.Delivered);
        m.Events.ShouldContain(e => e.Detail != null && e.Detail.StartsWith("Reconciled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sns_subscription_is_confirmed_only_when_signed()
    {
        using var confirm = TestSigning.SesRequest(null, type: "SubscriptionConfirmation");
        var before = TestSigning.Sns.Confirmed.Count;

        (await fixture.ClientWithKey(null).SendAsync(confirm)).StatusCode.ShouldBe(HttpStatusCode.OK);

        TestSigning.Sns.Confirmed.Count.ShouldBe(before + 1);
    }

    [Fact]
    public async Task Bulk_send_of_500_recipients_consumes_500_quota_units_once()
    {
        var tenant = await fixture.CreateTenantAsync();
        var recipients = Enumerable.Range(0, 500).Select(i => $"r{i}@example.test").ToArray();
        var body = new { recipients, subject = "Bulk", text = "Hi" };

        using var first = new HttpRequestMessage(HttpMethod.Post, "/v1/messages/bulk") { Content = JsonContent.Create(body) };
        first.Headers.Add("Idempotency-Key", "bulk-1");
        var response = await tenant.Http.SendAsync(first);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var replay = new HttpRequestMessage(HttpMethod.Post, "/v1/messages/bulk") { Content = JsonContent.Create(body) };
        replay.Headers.Add("Idempotency-Key", "bulk-1");
        (await tenant.Http.SendAsync(replay)).Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);

        await fixture.WithDbAsync(async db =>
        {
            (await db.Messages.CountAsync(m => m.TenantId == tenant.TenantId)).ShouldBe(500);
            (await db.Tenants.SingleAsync(t => t.Id == tenant.TenantId)).QuotaUsed.ShouldBe(500);
        });
    }

    [Fact]
    public async Task Concurrent_sends_cannot_push_usage_past_the_quota()
    {
        const int Limit = 50;
        var tenant = await fixture.CreateTenantAsync();
        await fixture.WithDbAsync(async db =>
        {
            (await db.Tenants.SingleAsync(t => t.Id == tenant.TenantId)).SetDailyQuota(Limit);
            await db.SaveChangesAsync();
        });

        var responses = await Task.WhenAll(Enumerable.Range(0, 200)
            .Select(i => tenant.Http.PostAsJsonAsync("/v1/messages", Msg($"q{i}@example.test"))));

        var other = responses.Where(r => r.StatusCode is not (HttpStatusCode.Accepted or HttpStatusCode.TooManyRequests)).ToList();
        other.ShouldBeEmpty(string.Join(" | ", await Task.WhenAll(other.Take(2).Select(async r => $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}"))));
        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).ShouldBe(Limit);
        var refused = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        refused.Count.ShouldBe(200 - Limit);
        refused.ShouldAllBe(r => r.Headers.RetryAfter != null);
        await fixture.WithDbAsync(async db =>
        {
            (await db.Tenants.SingleAsync(t => t.Id == tenant.TenantId)).QuotaUsed.ShouldBe(Limit);
            (await db.Messages.CountAsync(m => m.TenantId == tenant.TenantId)).ShouldBe(Limit);
        });
    }

    [Fact]
    public async Task Request_rate_limit_returns_429_with_retry_after()
    {
        var tenant = await fixture.CreateTenantAsync();
        await using var strict = new RelayApiFactory(fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["Relay:RateLimit:PermitsPerSecond"] = "1",
            ["Relay:RateLimit:Burst"] = "2",
        });
        var client = strict.CreateClient();
        client.DefaultRequestHeaders.Authorization = tenant.Http.DefaultRequestHeaders.Authorization;

        var statuses = new List<HttpResponseMessage>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add(await client.GetAsync("/v1/messages"));
        }

        statuses.Take(2).ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        var limited = statuses.Last();
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        limited.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task High_bounce_rate_pauses_the_tenant()
    {
        var tenant = await fixture.CreateTenantAsync();
        await using var guarded = new RelayApiFactory(fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["Relay:Reputation:MinimumVolume"] = "10",
        });
        var ids = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var providerId = $"re_{Guid.NewGuid():N}";
            await SentAsync(tenant, Unique($"v{i}"), "resend", providerId);
            ids.Add(providerId);
        }

        using var bounce = TestSigning.ResendRequest(ResendEvent("email.bounced", ids[0], "v0@example.test", "Permanent"));
        (await guarded.CreateClient().SendAsync(bounce)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var blocked = await tenant.Http.PostAsJsonAsync("/v1/messages", Msg(Unique("next")));
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var problem = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("Bounce rate");
    }
}
