using Relay.Application;

namespace Relay.UnitTests;

public class InMemoryUserServiceTests
{
    private readonly InMemoryUserService _service = new();

    [Fact]
    public async Task CreateUserAsync_issues_a_key_that_validates()
    {
        var user = await _service.CreateUserAsync("new@example.test", "New User");

        Assert.True(await _service.ValidateApiKeyAsync(user.ApiKey));
        Assert.Equal(user.Id, (await _service.GetUserByApiKeyAsync(user.ApiKey))?.Id);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_rejects_unknown_key() =>
        Assert.False(await _service.ValidateApiKeyAsync("not-a-key"));

    [Fact]
    public async Task CheckRateLimitAsync_blocks_once_daily_limit_is_reached()
    {
        var user = await _service.CreateUserAsync("limit@example.test", "Limit User");
        user.DailyEmailLimit = 2;

        // Same order as the controller: check, then increment.
        Assert.True(await _service.CheckRateLimitAsync(user.Id));
        await _service.IncrementEmailCountAsync(user.Id);

        Assert.True(await _service.CheckRateLimitAsync(user.Id));
        await _service.IncrementEmailCountAsync(user.Id);

        Assert.False(await _service.CheckRateLimitAsync(user.Id));
    }

    [Fact]
    public async Task CheckRateLimitAsync_is_false_for_unknown_user() =>
        Assert.False(await _service.CheckRateLimitAsync(Guid.NewGuid()));
}
