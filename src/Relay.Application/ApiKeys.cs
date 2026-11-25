using System.Security.Cryptography;
using System.Text;
using Relay.Domain;

namespace Relay.Application;

public interface IApiKeyStore
{
    /// <summary>Returns the key only if it is not revoked and its tenant is active.</summary>
    Task<ApiKey?> FindActiveByPrefixAsync(string prefix, CancellationToken cancellationToken);

    Task<ApiKey?> FindByPrefixAsync(string prefix, CancellationToken cancellationToken);

    Task AddAsync(ApiKey key, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface ITenantStore
{
    Task<Tenant?> FindByNameAsync(string name, CancellationToken cancellationToken);

    Task AddAsync(Tenant tenant, CancellationToken cancellationToken);
}

public sealed record AuthenticatedKey(Guid KeyId, Guid TenantId, IReadOnlyList<string> Scopes);

public sealed record IssuedKey(ApiKey Key, string Plaintext);

/// <summary>
/// Key format: <c>rly_{8 char id}_{43 char secret}</c>. The first 12 characters are the
/// visible prefix used for lookup; the SHA-256 of the whole key is compared in constant time.
/// </summary>
public static class ApiKeyFormat
{
    public const string Marker = "rly_";
    public const int PrefixLength = 12;
    private const string IdAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    public static (string Prefix, string Plaintext) Generate()
    {
        var id = RandomNumberGenerator.GetString(IdAlphabet, 8);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var prefix = Marker + id;
        return (prefix, $"{prefix}_{secret}");
    }

    public static bool TryGetPrefix(string? presented, out string prefix)
    {
        prefix = string.Empty;
        if (presented is null || presented.Length <= PrefixLength + 1
            || !presented.StartsWith(Marker, StringComparison.Ordinal) || presented[PrefixLength] != '_')
        {
            return false;
        }

        prefix = presented[..PrefixLength];
        return true;
    }

    public static string Hash(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
}

public sealed class ApiKeyService(IApiKeyStore keys, ITenantStore tenants, TimeProvider time)
{
    private static readonly byte[] DummyHash = new byte[32];

    public async Task<IssuedKey> IssueAsync(
        string tenantName, string keyName, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var unknown = scopes.Except(Scopes.All, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException($"Unknown scope(s): {string.Join(", ", unknown)}", nameof(scopes));
        }

        var now = time.GetUtcNow();
        var tenant = await tenants.FindByNameAsync(tenantName, cancellationToken);
        if (tenant is null)
        {
            tenant = Tenant.Create(Guid.CreateVersion7(now), tenantName, now);
            await tenants.AddAsync(tenant, cancellationToken);
        }

        var (prefix, plaintext) = ApiKeyFormat.Generate();
        var key = ApiKey.Create(Guid.CreateVersion7(now), tenant.Id, keyName, prefix, ApiKeyFormat.Hash(plaintext), scopes, now);
        await keys.AddAsync(key, cancellationToken);
        await keys.SaveChangesAsync(cancellationToken);
        return new IssuedKey(key, plaintext);
    }

    public async Task<bool> RevokeAsync(string prefix, CancellationToken cancellationToken)
    {
        var key = await keys.FindByPrefixAsync(prefix, cancellationToken);
        if (key is null)
        {
            return false;
        }

        key.Revoke(time.GetUtcNow());
        await keys.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<AuthenticatedKey?> AuthenticateAsync(string? presented, CancellationToken cancellationToken)
    {
        if (!ApiKeyFormat.TryGetPrefix(presented, out var prefix))
        {
            return null;
        }

        var key = await keys.FindActiveByPrefixAsync(prefix, cancellationToken);
        var expected = key is null ? DummyHash : Convert.FromHexString(key.Hash);
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(presented!));
        var matches = CryptographicOperations.FixedTimeEquals(expected, actual);
        return key is not null && matches ? new AuthenticatedKey(key.Id, key.TenantId, key.Scopes) : null;
    }
}
