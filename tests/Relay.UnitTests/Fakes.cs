using Relay.Application;
using Relay.Domain;

namespace Relay.UnitTests;

internal sealed class FakeMessageRepository : IMessageRepository
{
    public List<Message> Saved { get; } = [];

    public Task<Message?> AddAcceptedAsync(Message message, CancellationToken cancellationToken)
    {
        var existing = Saved.FirstOrDefault(m => m.TenantId == message.TenantId
            && message.IdempotencyKey is not null && m.IdempotencyKey == message.IdempotencyKey);
        if (existing is null)
        {
            Saved.Add(message);
        }

        return Task.FromResult(existing);
    }

    public Task<Message?> FindAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        Task.FromResult(Saved.FirstOrDefault(m => m.TenantId == tenantId && m.Id == messageId));

    public Task<Message?> FindByIdempotencyKeyAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(Saved.FirstOrDefault(m => m.TenantId == tenantId && m.IdempotencyKey == idempotencyKey));

    public Task<IReadOnlyList<Message>> ListAsync(Guid tenantId, Guid? before, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Message>>([.. Saved.Where(m => m.TenantId == tenantId)]);
}

internal sealed class FakeKeyStores : IApiKeyStore, ITenantStore
{
    public List<ApiKey> Keys { get; } = [];
    public List<Tenant> Tenants { get; } = [];

    public Task<ApiKey?> FindActiveByPrefixAsync(string prefix, CancellationToken cancellationToken) =>
        Task.FromResult(Keys.FirstOrDefault(k => k.Prefix == prefix && k.IsActive
            && Tenants.Any(t => t.Id == k.TenantId && t.IsActive)));

    public Task<ApiKey?> FindByPrefixAsync(string prefix, CancellationToken cancellationToken) =>
        Task.FromResult(Keys.FirstOrDefault(k => k.Prefix == prefix));

    public Task AddAsync(ApiKey key, CancellationToken cancellationToken)
    {
        Keys.Add(key);
        return Task.CompletedTask;
    }

    public Task<Tenant?> FindByNameAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(Tenants.FirstOrDefault(t => t.Name == name));

    public Task AddAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        Tenants.Add(tenant);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
