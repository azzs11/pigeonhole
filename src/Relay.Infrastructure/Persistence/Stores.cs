using Microsoft.EntityFrameworkCore;
using Npgsql;
using Relay.Application;
using Relay.Domain;

namespace Relay.Infrastructure.Persistence;

internal sealed class MessageRepository(RelayDbContext db) : IMessageRepository
{
    public async Task<Message?> AddAcceptedAsync(Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        db.Messages.Add(message);
        db.Outbox.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(message.CreatedAt),
            MessageId = message.Id,
            TenantId = message.TenantId,
            CreatedAt = message.CreatedAt,
            AvailableAt = message.CreatedAt,
        });

        try
        {
            // One SaveChanges = one transaction: the message and its outbox row commit together.
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: RelayDbContext.IdempotencyIndex,
        })
        {
            db.ChangeTracker.Clear();
            return await FindByIdempotencyKeyAsync(message.TenantId, message.IdempotencyKey!, cancellationToken);
        }
    }

    public Task<Message?> FindAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        db.Messages.AsNoTracking()
            .Include(m => m.Events.OrderBy(e => e.Sequence))
            .SingleOrDefaultAsync(m => m.TenantId == tenantId && m.Id == messageId, cancellationToken);

    public Task<Message?> FindByIdempotencyKeyAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken) =>
        db.Messages.AsNoTracking()
            .SingleOrDefaultAsync(m => m.TenantId == tenantId && m.IdempotencyKey == idempotencyKey, cancellationToken);

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
