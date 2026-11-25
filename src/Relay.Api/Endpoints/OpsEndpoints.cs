using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Relay.Api.Auth;
using Relay.Application;
using Relay.Infrastructure.Persistence;
using Relay.Infrastructure.Sandbox;

namespace Relay.Api.Endpoints;

internal static class OpsEndpoints
{
    public static void MapOpsEndpoints(this IEndpointRouteBuilder app, SandboxOptions sandbox)
    {
        app.MapGet("/v1/stats", Stats).WithTags("Dashboard").RequireAuthorization(Scopes.MessagesRead);
        app.MapGet("/v1/providers", Providers).WithTags("Dashboard").RequireAuthorization(Scopes.MessagesRead);
        app.MapGet("/v1/sandbox", () => TypedResults.Ok(new SandboxInfo(sandbox.Enabled, sandbox.MailpitApi?.ToString())))
            .WithTags("Sandbox").RequireAuthorization(Scopes.MessagesRead);
        if (!sandbox.Enabled)
        {
            return;
        }

        var group = app.MapGroup("/v1/sandbox").WithTags("Sandbox").RequireAuthorization(Scopes.MessagesSend);
        group.MapGet("/switches", (SandboxService s, CancellationToken ct) => s.GetSwitchesAsync(ct));
        group.MapPut("/switches", (SwitchesDto body, SandboxService s, CancellationToken ct) => s.SetSwitchesAsync(body, ct));
        group.MapGet("/promises", (ClaimsPrincipal user, SandboxService s, CancellationToken ct) => s.CheckPromisesAsync(user.TenantId(), ct));
        group.MapPost("/batch", Batch);
        group.MapPost("/messages/{id:guid}/bounce", Bounce);
    }

    private static async Task<Ok<TenantStats>> Stats(ClaimsPrincipal user, OpsQueries ops, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ops.StatsAsync(user.TenantId(), cancellationToken));

    private static async Task<Ok<IReadOnlyList<ProviderHealth>>> Providers(OpsQueries ops, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ops.ProvidersAsync(cancellationToken));

    private static async Task<Results<Accepted<BatchResult>, ProblemHttpResult>> Batch(
        BatchRequest request, ClaimsPrincipal user, SandboxService sandbox, CancellationToken cancellationToken) =>
        await sandbox.SendBatchAsync(user.TenantId(), request.Count, cancellationToken) is SendAccepted accepted
            ? TypedResults.Accepted((string?)null, new BatchResult(accepted.Messages.Count))
            : TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The batch was not accepted (quota or pause).");

    private static async Task<Results<Ok<BounceResult>, ProblemHttpResult>> Bounce(
        Guid id, ClaimsPrincipal user, SandboxService sandbox, CancellationToken cancellationToken) =>
        await sandbox.BounceAsync(user.TenantId(), id, cancellationToken) switch
        {
            null => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Message not found."),
            Relay.Infrastructure.Webhooks.EventResult.RetryLater => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "The message is being sent right now; try again in a moment."),
            var result => TypedResults.Ok(new BounceResult(result.ToString()!)),
        };
}

internal sealed record SandboxInfo(bool Enabled, string? MailpitUrl);

internal sealed record BatchRequest(int Count = 200);

internal sealed record BatchResult(int Accepted);

internal sealed record BounceResult(string Result);
