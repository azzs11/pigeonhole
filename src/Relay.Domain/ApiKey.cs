namespace Relay.Domain;

/// <summary>
/// An API key. Only the SHA-256 hash of the full key and a short visible prefix
/// are stored; the plaintext is shown once when the key is created.
/// </summary>
public sealed class ApiKey
{
    private ApiKey()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Prefix { get; private set; } = string.Empty;
    public string Hash { get; private set; } = string.Empty;
    public IReadOnlyList<string> Scopes { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    public static ApiKey Create(
        Guid id, Guid tenantId, string name, string prefix, string hash, IReadOnlyList<string> scopes, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentNullException.ThrowIfNull(scopes);
        return new ApiKey
        {
            Id = id,
            TenantId = tenantId,
            Name = name,
            Prefix = prefix,
            Hash = hash,
            Scopes = [.. scopes],
            CreatedAt = now,
        };
    }

    public void Revoke(DateTimeOffset at) => RevokedAt ??= at;
}
