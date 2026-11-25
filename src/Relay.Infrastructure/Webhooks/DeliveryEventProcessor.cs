using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Relay.Application;
using Relay.Domain;
using Relay.Infrastructure.Persistence;

namespace Relay.Infrastructure.Webhooks;

public enum EventResult
{
    Applied,
    Duplicate,
    Unmatched,

    /// <summary>The message is mid-send; answer non-2xx so the provider redelivers later.</summary>
    RetryLater,
}

/// <summary>
/// Applies provider events exactly once: the provider_events row, state changes, outbox changes
/// and suppressions commit in one transaction, and the unique (provider, event_id) index turns a
/// replayed event into a no-op.
/// </summary>
public sealed class DeliveryEventProcessor(
    RelayDbContext db, ReputationGuard reputation, DeliveryOptions delivery, TimeProvider time)
{
    public async Task<EventResult> ProcessAsync(DeliveryEvent e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);
        var now = time.GetUtcNow();
        var message = await FindMessageAsync(e, cancellationToken);
        if (message?.Status == MessageStatus.Sending)
        {
            return EventResult.RetryLater;
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.ProviderEvents.Add(new ProviderEvent
        {
            Id = Guid.CreateVersion7(now),
            Provider = e.Provider,
            EventId = e.EventId,
            Kind = e.Kind.ToString(),
            MessageId = message?.Id,
            ReceivedAt = now,
        });

        if (message is not null && e.Kind != DeliveryEventKind.Ignored)
        {
            await ApplyAsync(e, message, now, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, RelayDbContext.ProviderEventIndex))
        {
            await tx.RollbackAsync(cancellationToken);
            return EventResult.Duplicate;
        }

        if (message is not null && e.Kind is DeliveryEventKind.HardBounce or DeliveryEventKind.Complaint)
        {
            await reputation.EvaluateAsync(message.TenantId, cancellationToken);
        }

        return message is null ? EventResult.Unmatched : EventResult.Applied;
    }

    private async Task<Message?> FindMessageAsync(DeliveryEvent e, CancellationToken cancellationToken)
    {
        var query = db.Messages.Include(m => m.Events);
        if (e.ProviderMessageId is not null)
        {
            var byProviderId = await query.FirstOrDefaultAsync(m => m.ProviderMessageId == e.ProviderMessageId, cancellationToken);
            if (byProviderId is not null)
            {
                return byProviderId;
            }
        }

        if (e.InternetMessageId is { Length: > 0 } raw)
        {
            var internetId = raw.StartsWith('<') ? raw : $"<{raw}>";
            return await query.FirstOrDefaultAsync(m => m.InternetMessageId == internetId, cancellationToken);
        }

        return null;
    }

    private async Task ApplyAsync(DeliveryEvent e, Message message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var outbox = await db.Outbox.SingleAsync(o => o.MessageId == message.Id, cancellationToken);

        // Reconciliation: any provider event for this message proves the provider has it,
        // so an Unknown message is Sent and must not be resent.
        if (message.Status == MessageStatus.Unknown)
        {
            message.Transition(MessageStatus.Sent, now, $"Reconciled: {e.Provider} reported {e.Detail}");
            outbox.ProcessedAt = now;
            outbox.LockedBy = null;
            outbox.LockedUntil = null;
            outbox.PinnedProvider = null;
        }

        switch (e.Kind)
        {
            case DeliveryEventKind.Delivered when message.Status == MessageStatus.Sent:
                message.Transition(MessageStatus.Delivered, now, $"Delivered ({e.Provider})");
                break;
            case DeliveryEventKind.HardBounce:
                if (!message.Transition(MessageStatus.Bounced, now, $"Hard bounce ({e.Detail})").Succeeded)
                {
                    message.Annotate($"Hard bounce after {message.Status} ({e.Detail})", now);
                }

                await SuppressAsync(message, e, SuppressionReason.HardBounce, now, cancellationToken);
                break;
            case DeliveryEventKind.SoftBounce when message.Status == MessageStatus.Sent && outbox.Attempts >= delivery.MaxAttempts:
                message.Transition(MessageStatus.Bounced, now, $"Soft bounce, attempts exhausted ({e.Detail})");
                break;
            case DeliveryEventKind.SoftBounce when message.Status == MessageStatus.Sent:
                var retryAt = now + Backoff.ForAttempt(outbox.Attempts, delivery, RandomNumberGenerator.GetInt32(1001) / 1000.0);
                message.Transition(MessageStatus.Accepted, now, $"Soft bounce ({e.Detail}); retry {retryAt:O}");
                outbox.ProcessedAt = null;
                outbox.AvailableAt = retryAt;
                outbox.LockedBy = null;
                outbox.LockedUntil = null;
                break;
            case DeliveryEventKind.Complaint:
                message.Annotate("Recipient complained", now);
                await SuppressAsync(message, e, SuppressionReason.Complaint, now, cancellationToken);
                break;
            default:
                break;
        }
    }

    private async Task SuppressAsync(
        Message message, DeliveryEvent e, SuppressionReason reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var addresses = (e.Recipients.Count > 0 ? e.Recipients : [.. message.To, .. message.Cc, .. message.Bcc])
            .Select(EmailAddress.Normalize)
            .Distinct(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            var reasonText = reason.ToString();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO suppressions (id, tenant_id, address, reason, created_at)
                VALUES ({Guid.CreateVersion7(now)}, {message.TenantId}, {address}, {reasonText}, {now})
                ON CONFLICT (tenant_id, address) DO NOTHING
                """, cancellationToken);
        }
    }
}

/// <summary>Pauses a tenant whose bounce or complaint rate crosses the configured threshold.</summary>
public sealed class ReputationGuard(RelayDbContext db, ReputationOptions options, TimeProvider time)
{
    public async Task EvaluateAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        var now = time.GetUtcNow();
        var since = now.AddHours(-options.WindowHours);
        MessageStatus[] reached = [MessageStatus.Sent, MessageStatus.Delivered, MessageStatus.Bounced];
        var recent = db.Messages.Where(m => m.TenantId == tenantId && m.CreatedAt >= since);
        var sent = await recent.CountAsync(m => reached.Contains(m.Status), cancellationToken);
        if (sent < options.MinimumVolume)
        {
            return;
        }

        var bounced = await recent.CountAsync(m => m.Status == MessageStatus.Bounced, cancellationToken);
        var complaints = await db.Suppressions.CountAsync(
            s => s.TenantId == tenantId && s.Reason == SuppressionReason.Complaint && s.CreatedAt >= since, cancellationToken);

        var bounceRate = (double)bounced / sent;
        var complaintRate = (double)complaints / sent;
        string? reason = bounceRate > options.MaxBounceRate
            ? $"Bounce rate {bounceRate:P1} over {options.WindowHours}h exceeds {options.MaxBounceRate:P1}."
            : complaintRate > options.MaxComplaintRate
                ? $"Complaint rate {complaintRate:P2} over {options.WindowHours}h exceeds {options.MaxComplaintRate:P2}."
                : null;
        if (reason is null)
        {
            return;
        }

        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, cancellationToken);
        tenant.PauseSending(reason, now);
        await db.SaveChangesAsync(cancellationToken);
    }
}
