using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Relay.Application;
using Relay.Domain;
using Relay.Infrastructure.Delivery;
using Shouldly;

namespace Relay.IntegrationTests;

[Collection(RelayTestGroup.Name)]
public class DeliveryEngineTests(RelayFixture fixture)
{
    private static readonly DeliveryOptions Fast = new() { ProviderRetries = 2, ProviderRetryDelayMilliseconds = 0, MaxAttempts = 3 };

    private static Action<IServiceCollection> Providers(params IEmailProvider[] providers) =>
        services => Array.ForEach(providers, p => services.AddSingleton(p));

    private static MessageStatus[] Timeline(Message m) => [.. m.Events.OrderBy(e => e.Sequence).Select(e => e.ToStatus)];

    [Fact]
    public async Task Sends_on_the_first_provider_and_completes_the_outbox_row()
    {
        var primary = new ScriptedProvider("primary", false, ProviderOutcome.Sent("p-1"));
        await using var h = await DeliveryHarness.CreateAsync(fixture, Fast, Providers(primary));
        var id = await h.AcceptAsync();

        (await h.Engine.RunOnceAsync(default)).ShouldBe(1);

        var (message, outbox) = await h.LoadAsync(id);
        message.Status.ShouldBe(MessageStatus.Sent);
        Timeline(message).ShouldBe([MessageStatus.Accepted, MessageStatus.Sending, MessageStatus.Sent]);
        message.ProviderName.ShouldBe("primary");
        message.ProviderMessageId.ShouldBe("p-1");
        outbox.ProcessedAt.ShouldNotBeNull();
        outbox.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Transient_failure_retries_then_fails_over_to_the_next_provider()
    {
        var down = new ScriptedProvider("down", false, ProviderOutcome.Transient("connection refused"));
        var backup = new ScriptedProvider("backup", false, ProviderOutcome.Sent("b-1"));
        await using var h = await DeliveryHarness.CreateAsync(fixture, Fast, Providers(down, backup));
        var id = await h.AcceptAsync();

        await h.Engine.RunOnceAsync(default);

        var (message, _) = await h.LoadAsync(id);
        message.Status.ShouldBe(MessageStatus.Sent);
        message.ProviderName.ShouldBe("backup");
        down.Calls.ShouldBe(1 + Fast.ProviderRetries);
    }

    [Fact]
    public async Task All_providers_down_reschedules_with_backoff_then_dead_letters()
    {
        var down = new ScriptedProvider("down", false, ProviderOutcome.Transient("503"));
        await using var h = await DeliveryHarness.CreateAsync(fixture, Fast with { ProviderRetries = 0 }, Providers(down));
        var id = await h.AcceptAsync();

        await h.Engine.RunOnceAsync(default);
        var (afterFirst, outbox) = await h.LoadAsync(id);
        afterFirst.Status.ShouldBe(MessageStatus.Accepted);
        outbox.AvailableAt.ShouldBeGreaterThan(h.Time!.GetUtcNow());
        (await h.Engine.RunOnceAsync(default)).ShouldBe(0, "not due yet");

        for (var attempt = 2; attempt <= Fast.MaxAttempts; attempt++)
        {
            h.Time.Advance(TimeSpan.FromHours(2));
            (await h.Engine.RunOnceAsync(default)).ShouldBe(1);
        }

        var (final, finalOutbox) = await h.LoadAsync(id);
        final.Status.ShouldBe(MessageStatus.DeadLettered);
        finalOutbox.Attempts.ShouldBe(Fast.MaxAttempts);
        finalOutbox.ProcessedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Rejection_dead_letters_without_failover()
    {
        var strict = new ScriptedProvider("strict", false, ProviderOutcome.Rejected("SMTP 550: recipient not accepted"));
        var backup = new ScriptedProvider("backup", false, ProviderOutcome.Sent("b"));
        await using var h = await DeliveryHarness.CreateAsync(fixture, Fast, Providers(strict, backup));
        var id = await h.AcceptAsync();

        await h.Engine.RunOnceAsync(default);

        (await h.LoadAsync(id)).Message.Status.ShouldBe(MessageStatus.DeadLettered);
        backup.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Unknown_outcome_with_hold_policy_stays_unknown_and_never_fails_over()
    {
        var flaky = new ScriptedProvider("flaky", false, ProviderOutcome.Unknown("connection lost after DATA"));
        var backup = new ScriptedProvider("backup", false, ProviderOutcome.Sent("b"));
        await using var h = await DeliveryHarness.CreateAsync(fixture, Fast, Providers(flaky, backup));
        var id = await h.AcceptAsync();

        await h.Engine.RunOnceAsync(default);

        var (message, outbox) = await h.LoadAsync(id);
        message.Status.ShouldBe(MessageStatus.Unknown);
        outbox.ProcessedAt.ShouldNotBeNull();
        backup.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Unknown_on_an_idempotent_provider_retries_on_that_provider_only()
    {
        var resendLike = new ScriptedProvider("idem", true, ProviderOutcome.Unknown("502"), ProviderOutcome.Sent("r-1"));
        var backup = new ScriptedProvider("backup", false, ProviderOutcome.Sent("b"));
        await using var h = await DeliveryHarness.CreateAsync(fixture, Fast, Providers(resendLike, backup));
        var id = await h.AcceptAsync();

        await h.Engine.RunOnceAsync(default);
        var (unknown, pinned) = await h.LoadAsync(id);
        unknown.Status.ShouldBe(MessageStatus.Unknown);
        pinned.PinnedProvider.ShouldBe("idem");

        h.Time!.Advance(TimeSpan.FromHours(1));
        await h.Engine.RunOnceAsync(default);

        var (message, _) = await h.LoadAsync(id);
        message.Status.ShouldBe(MessageStatus.Sent);
        message.ProviderName.ShouldBe("idem");
        Timeline(message).ShouldBe([
            MessageStatus.Accepted, MessageStatus.Sending, MessageStatus.Unknown,
            MessageStatus.Accepted, MessageStatus.Sending, MessageStatus.Sent]);
        backup.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Provider_timeout_is_unknown_not_failover()
    {
        var hanging = new HangingProvider("hanging");
        var backup = new ScriptedProvider("backup", false, ProviderOutcome.Sent("b"));
        await using var h = await DeliveryHarness.CreateAsync(
            fixture, Fast with { ProviderTimeoutSeconds = 1 }, Providers(hanging, backup), fakeTime: false);
        var id = await h.AcceptAsync();

        await h.Engine.RunOnceAsync(default);

        (await h.LoadAsync(id)).Message.Status.ShouldBe(MessageStatus.Unknown);
        backup.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Worker_crash_mid_send_is_detected_on_reclaim_as_unknown()
    {
        var provider = new ScriptedProvider("p", false, ProviderOutcome.Sent("p"));
        await using var h = await DeliveryHarness.CreateAsync(fixture, Fast, Providers(provider));
        var id = await h.AcceptAsync();
        await h.WithDbAsync(async db =>
        {
            // Simulate a worker that committed "Sending", then died before recording the outcome.
            var message = await db.Messages.Include(m => m.Events).SingleAsync(m => m.Id == id);
            message.Transition(MessageStatus.Sending, h.Time!.GetUtcNow(), "Attempt 1");
            var outbox = await db.Outbox.SingleAsync(o => o.MessageId == id);
            outbox.Attempts = 1;
            outbox.LockedBy = "dead-worker";
            outbox.LockedUntil = h.Time.GetUtcNow().AddSeconds(30);
            await db.SaveChangesAsync();
        });

        (await h.Engine.RunOnceAsync(default)).ShouldBe(0, "lease still held");
        h.Time!.Advance(TimeSpan.FromMinutes(1));
        (await h.Engine.RunOnceAsync(default)).ShouldBe(1);

        (await h.LoadAsync(id)).Message.Status.ShouldBe(MessageStatus.Unknown);
        provider.Calls.ShouldBe(0, "never resend blindly after a crash");
    }

    [Fact]
    public async Task Eight_workers_claim_ten_thousand_rows_exactly_once()
    {
        const int Total = 10_000;
        await using var h = await DeliveryHarness.CreateAsync(fixture, Fast, Providers());
        await h.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("""
            INSERT INTO tenants (id, name, is_active, created_at) VALUES (gen_random_uuid(), 'claim', true, now());
            INSERT INTO messages (id, tenant_id, "from", "to", cc, bcc, subject, status, created_at, updated_at, internet_message_id)
              SELECT gen_random_uuid(), (SELECT id FROM tenants), 'a@relay.test', ARRAY['b@example.test'], ARRAY[]::text[], ARRAY[]::text[],
                     's', 'Accepted', now(), now(), '<' || g || '@relay.test>'
              FROM generate_series(1, 10000) AS g;
            INSERT INTO outbox (id, message_id, tenant_id, created_at, available_at, attempts)
              SELECT gen_random_uuid(), id, tenant_id, now(), now() - interval '1 day', 0 FROM messages;
            """));
        var now = DateTimeOffset.UtcNow;

        var perWorker = await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            var mine = new List<Guid>();
            await using var scope = h.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Relay.Infrastructure.Persistence.RelayDbContext>();
            while (true)
            {
                var ids = await OutboxClaimer.ClaimAsync(db, $"w{w}", 100, now, TimeSpan.FromMinutes(5), default);
                if (ids.Count == 0)
                {
                    return mine;
                }

                mine.AddRange(ids);
                var array = ids.ToArray();
                await db.Database.ExecuteSqlAsync($"UPDATE outbox SET processed_at = now() WHERE id = ANY({array})");
            }
        })));

        var all = perWorker.SelectMany(x => x).ToList();
        all.Count.ShouldBe(Total);
        all.Distinct().Count().ShouldBe(Total);
        perWorker.Count(x => x.Count > 0).ShouldBeGreaterThan(1, "work should spread across workers");
    }
}

[Collection(RelayTestGroup.Name)]
public class DeliverySuppressionTests(RelayFixture fixture)
{
    [Fact]
    public async Task Recipient_suppressed_after_accept_is_rejected_without_calling_a_provider()
    {
        var provider = new ScriptedProvider("p", false, ProviderOutcome.Sent("p"));
        await using var h = await DeliveryHarness.CreateAsync(
            fixture, new DeliveryOptions(), s => s.AddSingleton<IEmailProvider>(provider));
        var id = await h.AcceptAsync();
        await h.WithDbAsync(async db =>
        {
            var m = await db.Messages.SingleAsync(x => x.Id == id);
            db.Suppressions.Add(Suppression.Create(Guid.NewGuid(), m.TenantId, "to@example.test", SuppressionReason.Manual, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });

        await h.Engine.RunOnceAsync(default);

        (await h.LoadAsync(id)).Message.Status.ShouldBe(MessageStatus.Rejected);
        provider.Calls.ShouldBe(0);
    }
}

[Collection(RelayTestGroup.Name)]
public class DeliveryTracingTests(RelayFixture fixture)
{
    [Fact]
    public async Task Worker_span_continues_the_trace_of_the_accepting_request()
    {
        var spans = new System.Collections.Concurrent.ConcurrentBag<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == RelayTelemetry.Name,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var provider = new ScriptedProvider("p", false, ProviderOutcome.Sent("p"));
        await using var h = await DeliveryHarness.CreateAsync(fixture, new DeliveryOptions(), s => s.AddSingleton<IEmailProvider>(provider));

        Guid id;
        System.Diagnostics.ActivityTraceId requestTrace;
        using (var request = RelayTelemetry.Source.StartActivity("test.request"))
        {
            requestTrace = request!.TraceId;
            id = await h.AcceptAsync();
        }

        (await h.LoadAsync(id)).Outbox.TraceParent.ShouldNotBeNull().ShouldContain(requestTrace.ToHexString());
        await h.Engine.RunOnceAsync(default);

        var deliver = spans.Single(a => a.OperationName == "relay.deliver" && Equals(a.GetTagItem("relay.message.id"), id));
        deliver.TraceId.ShouldBe(requestTrace);
        spans.ShouldContain(a => a.OperationName == "relay.provider.send" && a.ParentSpanId == deliver.SpanId);
    }

    [Fact]
    public async Task Retry_immediately_policy_resends_unknown_outcomes_at_once()
    {
        var flaky = new ScriptedProvider("p", false, ProviderOutcome.Unknown("timeout"), ProviderOutcome.Sent("p"));
        await using var h = await DeliveryHarness.CreateAsync(fixture,
            new DeliveryOptions { UnknownOutcome = UnknownOutcomePolicy.RetryImmediately, ProviderRetries = 0 },
            s => s.AddSingleton<IEmailProvider>(flaky));
        var id = await h.AcceptAsync();

        await h.Engine.RunOnceAsync(default);
        await h.Engine.RunOnceAsync(default);

        (await h.LoadAsync(id)).Message.Status.ShouldBe(MessageStatus.Sent);
        flaky.Calls.ShouldBe(2);
    }
}
