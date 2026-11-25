using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Relay.Application;
using Relay.Domain;
using Relay.Infrastructure;
using Relay.Infrastructure.Delivery;
using Relay.Infrastructure.Persistence;
using Relay.Infrastructure.Webhooks;

namespace MailChaos;

internal sealed record RunContext(string ConnectionString, int Messages, FaultPlan Plan, Ledger Ledger, TimeSpan Deadline)
{
    public ConcurrentDictionary<string, DateTimeOffset> Submitted { get; } = new(StringComparer.Ordinal);
    public int Unresolved { get; set; }
    public int ClientGaveUp;
}

internal interface IDeliverySystem
{
    string Name { get; }

    Task RunAsync(RunContext run, CancellationToken cancellationToken);
}

internal static class Shared
{
    public const int ClientRetries = 10;
    public const int Clients = 8;

    public static string Subject(int i) => $"m-{i:D6}";

    /// <summary>
    /// Database restarts happen at evenly spaced points in the submission sequence (rate x 20 per run),
    /// so fast and slow systems see them at the same stage of their work.
    /// </summary>
    public static HashSet<int> RestartPoints(RunContext run)
    {
        var restarts = (int)Math.Round(run.Plan.Rate * 20);
        return [.. Enumerable.Range(1, restarts).Select(k => k * run.Messages / (restarts + 1))];
    }

    /// <summary>Kills every other connection to the run's database, like a database restart.</summary>
    public static async Task RestartDatabaseAsync(RunContext run, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = new NpgsqlConnection(run.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(
                "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()", conn);
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            run.Plan.Count("DatabaseRestart");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    public static async Task ForEachClientAsync(RunContext run, Func<int, Task> send)
    {
        var messages = run.Messages;
        var restartAt = RestartPoints(run);
        var restarts = new List<Task>();
        var next = -1;
        await Task.WhenAll(Enumerable.Range(0, Clients).Select(_ => Task.Run(async () =>
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) < messages)
            {
                if (restartAt.Contains(i))
                {
                    lock (restarts)
                    {
                        restarts.Add(RestartDatabaseAsync(run, CancellationToken.None));
                    }
                }

                await send(i).ConfigureAwait(false);
            }
        }))).ConfigureAwait(false);
        await Task.WhenAll(restarts).ConfigureAwait(false);
    }
}

/// <summary>
/// System 1: send in the request path, then save. A crash or timeout after the provider
/// accepted looks like a failure to the client, which retries and sends again.
/// </summary>
internal sealed class NaiveSystem : IDeliverySystem
{
    public string Name => "naive send-then-save";

    public async Task RunAsync(RunContext run, CancellationToken cancellationToken)
    {
        await using (var conn = new NpgsqlConnection(run.ConnectionString))
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand("CREATE TABLE naive_sends (subject text PRIMARY KEY, sent_at timestamptz NOT NULL)", conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var provider = new ChaosProvider(run.Plan, run.Ledger, onAccepted: null);
        await Shared.ForEachClientAsync(run, async i =>
        {
            var subject = Shared.Subject(i);
            run.Submitted.TryAdd(subject, DateTimeOffset.UtcNow);
            for (var attempt = 0; attempt < Shared.ClientRetries; attempt++)
            {
                try
                {
                    var email = new OutgoingEmail(Guid.NewGuid(), $"<{Guid.NewGuid():N}@bench>", "a@relay.test", ["b@example.test"], [], [], subject, "x", null);
                    var outcome = await provider.SendAsync(email, cancellationToken).ConfigureAwait(false);
                    if (outcome.Kind != ProviderOutcomeKind.Sent)
                    {
                        throw new InvalidOperationException(outcome.Detail);
                    }

                    await using var conn = new NpgsqlConnection(run.ConnectionString);
                    await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await using var cmd = new NpgsqlCommand("INSERT INTO naive_sends VALUES (@s, now()) ON CONFLICT DO NOTHING", conn);
                    cmd.Parameters.AddWithValue("s", subject);
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception)
                {
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                }
            }

            Interlocked.Increment(ref run.ClientGaveUp);
        }).ConfigureAwait(false);
    }
}

/// <summary>
/// Systems 2 and 3: the real Relay engine. Without reconciliation, an unknown outcome is retried
/// at once (an outbox with no Unknown state). With it, unknown outcomes wait for a provider event,
/// or resend after the reconcile window if none arrives.
/// </summary>
internal sealed class OutboxSystem(bool reconcile) : IDeliverySystem
{
    private const int Workers = 4;

    public string Name => reconcile ? "outbox + unknown state" : "outbox only";

    public async Task RunAsync(RunContext run, CancellationToken cancellationToken)
    {
        var events = new ConcurrentQueue<(DateTimeOffset Due, DeliveryEvent Event)>();
        var services = new ServiceCollection().AddLogging(l => l.SetMinimumLevel(LogLevel.None));
        services.AddRelayCore(run.ConnectionString, new MessagingOptions { DefaultDailyQuota = int.MaxValue / 2 })
            .AddRelayDelivery(new DeliveryOptions
            {
                BatchSize = 20,
                LeaseSeconds = 1,
                MaxAttempts = 50,
                BackoffBaseSeconds = 0,
                ProviderRetries = 0,
                ProviderTimeoutSeconds = 5,
                UnknownOutcome = reconcile ? UnknownOutcomePolicy.RetryAfterWindow : UnknownOutcomePolicy.RetryImmediately,
                ReconcileWindowSeconds = 2,
            })
            .AddRelayWebhooks(new WebhookOptions(), new ReputationOptions { Enabled = false });
        services.AddSingleton<IEmailProvider>(new ChaosProvider(run.Plan, run.Ledger, reconcile
            ? (email, providerId) =>
            {
                // The provider's delivery webhook, 50-150 ms later, sometimes delivered twice.
                var e = new DeliveryEvent("chaos", Guid.NewGuid().ToString(), DeliveryEventKind.Delivered,
                    providerId, email.InternetMessageId, [], "Delivery");
                var due = DateTimeOffset.UtcNow.AddMilliseconds(run.Plan.Between(50, 150));
                events.Enqueue((due, e));
                if (run.Plan.Chance(run.Plan.Rate))
                {
                    run.Plan.Count("DuplicateWebhook");
                    events.Enqueue((due.AddMilliseconds(30), e));
                }
            }
        : null));
        await using var sp = services.BuildServiceProvider();

        Guid tenantId;
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RelayDbContext>().Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            tenantId = (await scope.ServiceProvider.GetRequiredService<ApiKeyService>()
                .IssueAsync("chaos", "k", Scopes.All, cancellationToken).ConfigureAwait(false)).Key.TenantId;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var background = new List<Task>();
        background.AddRange(Enumerable.Range(0, Workers).Select(_ => WorkerLoopAsync(sp, stop.Token)));
        if (reconcile)
        {
            background.Add(WebhookPumpAsync(sp, events, stop.Token));
        }

        await Shared.ForEachClientAsync(run, i => SubmitAsync(sp, run, tenantId, i, cancellationToken)).ConfigureAwait(false);

        // Drain: wait until every message is final, or the deadline passes.
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < run.Deadline)
        {
            var open = await CountOpenAsync(sp, cancellationToken).ConfigureAwait(false);
            if (open == 0 && events.IsEmpty)
            {
                break;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        run.Unresolved = await CountOpenAsync(sp, cancellationToken).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(background.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default))).ConfigureAwait(false);
    }

    private static async Task SubmitAsync(ServiceProvider sp, RunContext run, Guid tenantId, int i, CancellationToken cancellationToken)
    {
        var subject = Shared.Subject(i);
        run.Submitted.TryAdd(subject, DateTimeOffset.UtcNow);
        var request = new SendMessageRequest { To = ["b@example.test"], Subject = subject, Text = "x" };
        for (var attempt = 0; attempt < Shared.ClientRetries; attempt++)
        {
            try
            {
                await using var scope = sp.CreateAsyncScope();
                var outcome = await scope.ServiceProvider.GetRequiredService<SendMessageHandler>()
                    .HandleAsync(tenantId, request, $"k-{i}", cancellationToken).ConfigureAwait(false);
                if (outcome is SendAccepted)
                {
                    return;
                }
            }
            catch (Exception)
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
        }

        Interlocked.Increment(ref run.ClientGaveUp);
    }

    private static async Task WorkerLoopAsync(ServiceProvider sp, CancellationToken cancellationToken)
    {
        var engine = ActivatorUtilities.CreateInstance<DeliveryEngine>(sp);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await engine.RunOnceAsync(cancellationToken).ConfigureAwait(false) == 0)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (SimulatedCrashException)
            {
                // The process died mid-batch: its leases stay until they expire. Restart with a new identity.
                engine = ActivatorUtilities.CreateInstance<DeliveryEngine>(sp);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task WebhookPumpAsync(
        ServiceProvider sp, ConcurrentQueue<(DateTimeOffset Due, DeliveryEvent Event)> events, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!events.TryDequeue(out var item))
            {
                await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            if (item.Due > DateTimeOffset.UtcNow)
            {
                events.Enqueue(item);
                await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            try
            {
                await using var scope = sp.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<DeliveryEventProcessor>()
                    .ProcessAsync(item.Event, cancellationToken).ConfigureAwait(false);
                if (result == EventResult.RetryLater)
                {
                    events.Enqueue((DateTimeOffset.UtcNow.AddMilliseconds(200), item.Event));
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Providers redeliver on any non-2xx.
                events.Enqueue((DateTimeOffset.UtcNow.AddMilliseconds(100), item.Event));
            }
        }
    }

    private static async Task<int> CountOpenAsync(ServiceProvider sp, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var scope = sp.CreateAsyncScope();
                MessageStatus[] open = [MessageStatus.Accepted, MessageStatus.Sending, MessageStatus.Unknown];
                return await scope.ServiceProvider.GetRequiredService<RelayDbContext>().Messages
                    .CountAsync(m => open.Contains(m.Status), cancellationToken).ConfigureAwait(false);
            }
            catch (NpgsqlException) when (attempt < 5)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
