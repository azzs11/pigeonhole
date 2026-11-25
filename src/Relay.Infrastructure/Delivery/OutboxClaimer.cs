using Microsoft.EntityFrameworkCore;
using Relay.Infrastructure.Persistence;

namespace Relay.Infrastructure.Delivery;

public static class OutboxClaimer
{
    /// <summary>
    /// Claims up to <paramref name="batchSize"/> due rows for <paramref name="workerId"/> with a lease.
    /// SKIP LOCKED lets many workers claim concurrently without blocking or double-claiming;
    /// an expired lease (crashed worker) makes a row claimable again.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ClaimAsync(
        RelayDbContext db, string workerId, int batchSize, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var lockedUntil = now + lease;
        return await db.Database.SqlQuery<Guid>($"""
            UPDATE outbox AS o
            SET locked_by = {workerId}, locked_until = {lockedUntil}
            FROM (
                SELECT id FROM outbox
                WHERE processed_at IS NULL
                  AND available_at <= {now}
                  AND (locked_until IS NULL OR locked_until < {now})
                ORDER BY available_at
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            ) AS due
            WHERE o.id = due.id
            RETURNING o.id AS "Value"
            """).ToListAsync(cancellationToken);
    }
}
