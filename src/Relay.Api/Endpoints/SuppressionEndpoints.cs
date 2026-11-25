using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Relay.Api.Auth;
using Relay.Application;
using Relay.Domain;

namespace Relay.Api.Endpoints;

internal static class SuppressionEndpoints
{
    public static void MapSuppressionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/suppressions").WithTags("Suppressions");
        group.MapGet("/", List).RequireAuthorization(Scopes.MessagesRead);
        group.MapDelete("/{address}", Remove).RequireAuthorization(Scopes.MessagesSend);
    }

    private static async Task<Results<Ok<SuppressionPage>, ValidationProblem>> List(
        ClaimsPrincipal user, ISuppressionStore store, CancellationToken cancellationToken, string? after = null, int limit = 100)
    {
        if (limit is < 1 or > 500)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["limit"] = ["Must be 1-500."] });
        }

        var items = await store.ListAsync(user.TenantId(), limit, after, cancellationToken);
        return TypedResults.Ok(new SuppressionPage(
            [.. items.Select(s => new SuppressionResponse(s.Address, s.Reason, s.CreatedAt))],
            items.Count == limit ? items[^1].Address : null));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Remove(
        string address, ClaimsPrincipal user, ISuppressionStore store, CancellationToken cancellationToken) =>
        await store.RemoveAsync(user.TenantId(), address, cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Address is not suppressed.");
}

internal sealed record SuppressionResponse(string Address, SuppressionReason Reason, DateTimeOffset CreatedAt);

internal sealed record SuppressionPage(IReadOnlyList<SuppressionResponse> Items, string? NextAfter);
