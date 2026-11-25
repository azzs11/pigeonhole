using Relay.Application;
using Relay.Domain;

namespace Relay.UnitTests;

internal sealed class FakeMessageRepository : IMessageRepository
{
    public List<Message> Saved { get; } = [];
    public int QuotaLimit { get; set; } = int.MaxValue;
    public int QuotaUsed { get; private set; }
    public string? PausedReason { get; set; }

    public Task<AddResult> AddAcceptedAsync(IReadOnlyList<Message> messages, int quotaUnits, CancellationToken cancellationToken)
    {
        if (PausedReason is not null)
        {
            return Task.FromResult<AddResult>(new AddPaused(PausedReason));
        }

        if (QuotaUsed + quotaUnits > QuotaLimit)
        {
            return Task.FromResult<AddResult>(new AddQuotaExceeded(TimeSpan.FromHours(1)));
        }

        QuotaUsed += quotaUnits;
        Saved.AddRange(messages);
        return Task.FromResult<AddResult>(new AddSucceeded());
    }

    public Task<Message?> FindAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        Task.FromResult(Saved.FirstOrDefault(m => m.TenantId == tenantId && m.Id == messageId));

    public Task<IReadOnlyList<Message>> FindByIdempotencyKeyAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Message>>([.. Saved.Where(m => m.TenantId == tenantId
            && (m.IdempotencyKey == idempotencyKey || (m.IdempotencyKey?.StartsWith(idempotencyKey + "#", StringComparison.Ordinal) ?? false)))]);

    public Task<IReadOnlyList<Message>> ListAsync(Guid tenantId, Guid? before, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Message>>([.. Saved.Where(m => m.TenantId == tenantId)]);
}

internal sealed class FakeSuppressionStore : ISuppressionStore
{
    public HashSet<string> Addresses { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<string>> FindSuppressedAsync(Guid tenantId, IEnumerable<string> addresses, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([.. addresses.Select(EmailAddress.Normalize).Where(Addresses.Contains).Distinct()]);

    public Task<IReadOnlyList<Suppression>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Suppression>>([]);

    public Task<bool> RemoveAsync(Guid tenantId, string address, CancellationToken cancellationToken) =>
        Task.FromResult(Addresses.Remove(EmailAddress.Normalize(address)));
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
