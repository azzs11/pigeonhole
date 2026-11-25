using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Relay.Application;
using Relay.Domain;
using Relay.Infrastructure.Persistence;

namespace Relay.Infrastructure.Delivery;

/// <summary>
/// Claims due outbox rows and drives each message through the state machine.
/// "Sending" is committed before the provider is called, so a crash mid-send is
/// detected on reclaim and becomes Unknown instead of a silent resend.
/// </summary>
public sealed partial class DeliveryEngine(
    IServiceScopeFactory scopes, ProviderChain providers, DeliveryOptions options, TimeProvider time, ILogger<DeliveryEngine> logger)
{
    public string WorkerId { get; } = $"{Environment.MachineName}-{Guid.NewGuid():N}"[..Math.Min(100, Environment.MachineName.Length + 33)];

    /// <summary>Claims one batch and processes it. Returns the number of rows claimed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> claimed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            claimed = await OutboxClaimer.ClaimAsync(
                db, WorkerId, options.BatchSize, time.GetUtcNow(), TimeSpan.FromSeconds(options.LeaseSeconds), cancellationToken);
        }

        foreach (var outboxId in claimed)
        {
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                await ProcessAsync(scope.ServiceProvider.GetRequiredService<RelayDbContext>(), outboxId, cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                LogLostLease(logger, outboxId);
            }
        }

        return claimed.Count;
    }

    private async Task ProcessAsync(RelayDbContext db, Guid outboxId, CancellationToken cancellationToken)
    {
        var entry = await db.Outbox.SingleOrDefaultAsync(o => o.Id == outboxId && o.LockedBy == WorkerId, cancellationToken);
        if (entry is null)
        {
            return;
        }

        var message = await db.Messages.Include(m => m.Events).SingleAsync(m => m.Id == entry.MessageId, cancellationToken);
        var now = time.GetUtcNow();
        switch (message.Status)
        {
            case MessageStatus.Sending:
                message.Transition(MessageStatus.Unknown, now, "Worker stopped during the previous attempt; outcome unknown");
                ApplyUnknown(entry, message.ProviderName, now);
                await db.SaveChangesAsync(cancellationToken);
                return;
            case MessageStatus.Unknown:
                message.Transition(MessageStatus.Accepted, now, entry.PinnedProvider is null
                    ? "Retrying after the reconcile window"
                    : $"Retrying on {entry.PinnedProvider}, which deduplicates");
                break;
            case MessageStatus.Accepted:
                break;
            default:
                Complete(entry, now);
                await db.SaveChangesAsync(cancellationToken);
                return;
        }

        entry.Attempts++;
        message.Transition(MessageStatus.Sending, now, $"Attempt {entry.Attempts}");
        await db.SaveChangesAsync(cancellationToken);

        var (outcome, provider) = await providers.SendAsync(OutgoingEmail.Create(message), entry.PinnedProvider, cancellationToken);
        now = time.GetUtcNow();
        message.RecordProvider(provider, outcome.ProviderMessageId);
        switch (outcome.Kind)
        {
            case ProviderOutcomeKind.Sent:
                message.Transition(MessageStatus.Sent, now, $"Sent via {provider}");
                Complete(entry, now);
                break;
            case ProviderOutcomeKind.Rejected:
                message.Transition(MessageStatus.DeadLettered, now, $"Rejected by {provider}: {outcome.Detail}");
                Complete(entry, now);
                break;
            case ProviderOutcomeKind.Transient when entry.Attempts >= options.MaxAttempts:
                message.Transition(MessageStatus.DeadLettered, now, $"Gave up after {entry.Attempts} attempts: {outcome.Detail}");
                Complete(entry, now);
                break;
            case ProviderOutcomeKind.Transient:
                var retryAt = now + Backoff.ForAttempt(entry.Attempts, options, RandomNumberGenerator.GetInt32(1001) / 1000.0);
                message.Transition(MessageStatus.Accepted, now, $"Not sent ({outcome.Detail}); retry {retryAt:O}");
                Reschedule(entry, retryAt, pinnedProvider: null);
                break;
            case ProviderOutcomeKind.Unknown:
                message.Transition(MessageStatus.Unknown, now, $"Outcome unknown at {provider}: {outcome.Detail}");
                ApplyUnknown(entry, provider, now);
                break;
            default:
                throw new InvalidOperationException($"Unhandled outcome {outcome.Kind}.");
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private void ApplyUnknown(OutboxMessage entry, string? provider, DateTimeOffset now)
    {
        if (entry.Attempts < options.MaxAttempts && providers.IsIdempotent(provider))
        {
            Reschedule(entry, now + Backoff.ForAttempt(entry.Attempts, options, 0.5), provider);
        }
        else if (entry.Attempts < options.MaxAttempts && options.UnknownOutcome == UnknownOutcomePolicy.RetryAfterWindow)
        {
            Reschedule(entry, now + TimeSpan.FromSeconds(options.ReconcileWindowSeconds), pinnedProvider: null);
        }
        else
        {
            Complete(entry, now);
        }
    }

    private static void Complete(OutboxMessage entry, DateTimeOffset now)
    {
        entry.ProcessedAt = now;
        entry.LockedBy = null;
        entry.LockedUntil = null;
    }

    private static void Reschedule(OutboxMessage entry, DateTimeOffset at, string? pinnedProvider)
    {
        entry.AvailableAt = at;
        entry.PinnedProvider = pinnedProvider;
        entry.LockedBy = null;
        entry.LockedUntil = null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the lease on outbox row {OutboxId}; another worker owns it")]
    private static partial void LogLostLease(ILogger logger, Guid outboxId);
}
