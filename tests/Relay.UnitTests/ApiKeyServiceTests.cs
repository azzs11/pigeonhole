using Relay.Application;
using Shouldly;

namespace Relay.UnitTests;

public class ApiKeyServiceTests
{
    private readonly FakeKeyStores _stores = new();
    private readonly ApiKeyService _service;

    public ApiKeyServiceTests() => _service = new ApiKeyService(_stores, _stores, TimeProvider.System);

    [Fact]
    public async Task Issued_key_is_stored_only_as_hash_and_prefix()
    {
        var issued = await _service.IssueAsync("acme", "ci", Scopes.All, default);

        issued.Plaintext.ShouldStartWith(issued.Key.Prefix + "_");
        issued.Key.Hash.ShouldBe(ApiKeyFormat.Hash(issued.Plaintext));
        issued.Key.Hash.ShouldNotContain(issued.Plaintext);
        issued.Plaintext.Length.ShouldBeGreaterThan(50);
    }

    [Fact]
    public async Task Correct_key_authenticates_with_tenant_and_scopes()
    {
        var issued = await _service.IssueAsync("acme", "ci", [Scopes.MessagesRead], default);

        var auth = (await _service.AuthenticateAsync(issued.Plaintext, default)).ShouldNotBeNull();

        auth.TenantId.ShouldBe(issued.Key.TenantId);
        auth.Scopes.ShouldBe([Scopes.MessagesRead]);
    }

    [Fact]
    public async Task Right_prefix_wrong_secret_is_rejected()
    {
        var issued = await _service.IssueAsync("acme", "ci", Scopes.All, default);
        var tampered = issued.Plaintext[..^1] + (issued.Plaintext[^1] == 'A' ? 'B' : 'A');

        (await _service.AuthenticateAsync(tampered, default)).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test-api-key-12345")]
    [InlineData("rly_")]
    public async Task Malformed_keys_are_rejected(string? key) =>
        (await _service.AuthenticateAsync(key, default)).ShouldBeNull();

    [Fact]
    public async Task Revoked_key_is_rejected()
    {
        var issued = await _service.IssueAsync("acme", "ci", Scopes.All, default);

        (await _service.RevokeAsync(issued.Key.Prefix, default)).ShouldBeTrue();

        (await _service.AuthenticateAsync(issued.Plaintext, default)).ShouldBeNull();
    }

    [Fact]
    public async Task Unknown_scope_is_refused() =>
        await Should.ThrowAsync<ArgumentException>(() => _service.IssueAsync("acme", "ci", ["admin"], default));
}
