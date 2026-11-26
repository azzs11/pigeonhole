using Microsoft.EntityFrameworkCore;
using Npgsql;
using Relay.Application;
using Relay.Domain;

namespace Relay.Infrastructure.Persistence;

internal static class PostgresErrors
{
    public static bool IsUniqueViolation(DbUpdateException ex, string constraint) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && string.Equals(pg.ConstraintName, constraint, StringComparison.Ordinal);
}

internal sealed class MessageRepository(RelayDbContext db) : IMessageRepository
{
    public async Task<AddResult> AddAcceptedAsync(IReadOnlyList<Message> messages, int quotaUnits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var first = messages[0];
        var now = first.CreatedAt;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        db.Messages.AddRange(messages);
        // Held messages (awaiting approval) get their outbox row only when approved.
        db.Outbox.AddRange(messages.Where(m => m.Status == MessageStatus.Accepted).Select(m => new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            MessageId = m.Id,
            TenantId = m.TenantId,
            CreatedAt = now,
            AvailableAt = now,
            TraceParent = System.Diagnostics.Activity.Current is { IdFormat: System.Diagnostics.ActivityIdFormat.W3C } a ? a.Id : null,
        }));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, RelayDbContext.IdempotencyIndex))
        {
            // A concurrent request with the same key won; its quota stands, ours rolls back.
            await tx.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var key = first.IdempotencyKey!;
            var baseKey = key.Contains('#', StringComparison.Ordinal) ? key[..key.LastIndexOf('#')] : key;
            return new AddDuplicate(await FindByIdempotencyKeyAsync(first.TenantId, baseKey, cancellationToken));
        }

        // Charge the quota last: one statement that resets on a new UTC day and refuses if paused or
        // over quota. Concurrent requests serialize on the tenant row, so usage can never pass the
        // limit, and because this runs just before COMMIT the row lock is held for one statement and
        // the commit, not for the inserts (see docs/testing.md, "hot tenant row"). Refusal rolls the
        // inserts back, so the whole accept stays atomic.
        var used = await db.Database.SqlQuery<int>($"""
            UPDATE tenants
            SET quota_used = CASE WHEN quota_day = {today} THEN quota_used + {quotaUnits} ELSE {quotaUnits} END,
                quota_day = {today}
            WHERE id = {first.TenantId}
              AND sending_paused_at IS NULL
              AND (CASE WHEN quota_day = {today} THEN quota_used ELSE 0 END) + {quotaUnits} <= daily_quota
            RETURNING quota_used AS "Value"
            """).ToListAsync(cancellationToken);
        if (used.Count == 0)
        {
            await tx.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == first.TenantId, cancellationToken);
            return tenant.IsSendingPaused
                ? new AddPaused(tenant.PauseReason ?? "Sending is paused.")
                : new AddQuotaExceeded(today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - now.UtcDateTime);
        }

        await tx.CommitAsync(cancellationToken);
        return new AddSucceeded();
    }

    public Task<Message?> FindAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        db.Messages.AsNoTracking()
            .Include(m => m.Events.OrderBy(e => e.Sequence))
            .SingleOrDefaultAsync(m => m.TenantId == tenantId && m.Id == messageId, cancellationToken);

    public async Task<IReadOnlyList<Message>> FindByIdempotencyKeyAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken)
    {
        // Fast path, run on every send: two point lookups on the unique (tenant_id, idempotency_key)
        // index. A bulk request stores children as "{key}#0".."{key}#n-1" in one transaction, so "#0"
        // exists whenever any child does. Only a replay (rare) needs the prefix query below, which a
        // B-tree cannot serve and which used to scan the whole table on every send.
        var bulkFirst = idempotencyKey + "#0";
        if (!await db.Messages.AnyAsync(
                m => m.TenantId == tenantId && (m.IdempotencyKey == idempotencyKey || m.IdempotencyKey == bulkFirst), cancellationToken))
        {
            return [];
        }

        var bulkPrefix = idempotencyKey + "#";
        return await db.Messages.AsNoTracking()
            .Where(m => m.TenantId == tenantId
                && (m.IdempotencyKey == idempotencyKey || m.IdempotencyKey!.StartsWith(bulkPrefix)))
            .OrderBy(m => m.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Message>> ListAsync(Guid tenantId, Guid? before, int limit, CancellationToken cancellationToken)
    {
        var query = db.Messages.AsNoTracking().Where(m => m.TenantId == tenantId);
        if (before is { } cursor)
        {
            query = query.Where(m => m.Id < cursor);
        }

        return await query.OrderByDescending(m => m.Id).Take(limit).ToListAsync(cancellationToken);
    }
}

internal sealed class SuppressionStore(RelayDbContext db) : ISuppressionStore
{
    public async Task<IReadOnlyList<string>> FindSuppressedAsync(
        Guid tenantId, IEnumerable<string> addresses, CancellationToken cancellationToken)
    {
        var normalized = addresses.Select(EmailAddress.Normalize).Distinct(StringComparer.Ordinal).ToList();
        return await db.Suppressions.AsNoTracking()
            .Where(s => s.TenantId == tenantId && normalized.Contains(s.Address))
            .Select(s => s.Address)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Suppression>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken)
    {
        var query = db.Suppressions.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (after is not null)
        {
            query = query.Where(s => s.Address.CompareTo(after) > 0);
        }

        return await query.OrderBy(s => s.Address).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<bool> RemoveAsync(Guid tenantId, string address, CancellationToken cancellationToken)
    {
        var normalized = EmailAddress.Normalize(address);
        return await db.Suppressions
            .Where(s => s.TenantId == tenantId && s.Address == normalized)
            .ExecuteDeleteAsync(cancellationToken) > 0;
    }
}

internal sealed class ApiKeyStore(RelayDbContext db) : IApiKeyStore
{
    public Task<ApiKey?> FindActiveByPrefixAsync(string prefix, CancellationToken cancellationToken) =>
        db.ApiKeys.AsNoTracking()
            .Where(k => k.Prefix == prefix && k.RevokedAt == null)
            .Where(k => db.Tenants.Any(t => t.Id == k.TenantId && t.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<ApiKey?> FindByPrefixAsync(string prefix, CancellationToken cancellationToken) =>
        db.ApiKeys.SingleOrDefaultAsync(k => k.Prefix == prefix, cancellationToken);

    public async Task AddAsync(ApiKey key, CancellationToken cancellationToken) =>
        await db.ApiKeys.AddAsync(key, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

internal sealed class TenantStore(RelayDbContext db) : ITenantStore
{
    public Task<Tenant?> FindByNameAsync(string name, CancellationToken cancellationToken) =>
        db.Tenants.SingleOrDefaultAsync(t => t.Name == name, cancellationToken);

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken) =>
        await db.Tenants.AddAsync(tenant, cancellationToken);
}
