using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Relay.Application;

namespace Relay.Api.Auth;

/// <summary>The one place an API key is checked. Expects <c>Authorization: Bearer rly_...</c>.</summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyService apiKeys,
    IProblemDetailsService problemDetails)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string TenantClaim = "tenant_id";
    public const string ScopeClaim = "scope";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var key = await apiKeys.AuthenticateAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (key is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, key.KeyId.ToString()),
            new(TenantClaim, key.TenantId.ToString()),
            .. key.Scopes.Select(s => new Claim(ScopeClaim, s)),
        ];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
        return WriteProblem(StatusCodes.Status401Unauthorized, "A valid API key is required.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblem(StatusCodes.Status403Forbidden, "This API key does not have the required scope.");

    private async Task WriteProblem(int status, string title)
    {
        Response.StatusCode = status;
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = { Status = status, Title = title },
        });
    }
}

internal static class ClaimsPrincipalExtensions
{
    public static Guid TenantId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ApiKeyAuthenticationHandler.TenantClaim)
                   ?? throw new InvalidOperationException("Authenticated principal has no tenant."));
}
