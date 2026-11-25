using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Relay.Api.Auth;
using Relay.Application;
using Relay.Domain;

namespace Relay.Api.Endpoints;

internal static class MessageEndpoints
{
    public static void MapMessageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/messages").WithTags("Messages");
        group.MapPost("/", Send).RequireAuthorization(Scopes.MessagesSend);
        group.MapPost("/bulk", SendBulk).RequireAuthorization(Scopes.MessagesSend);
        group.MapGet("/{id:guid}", Get).RequireAuthorization(Scopes.MessagesRead);
        group.MapGet("/{id:guid}/explain", Explain).RequireAuthorization(Scopes.MessagesRead);
        group.MapGet("/", List).RequireAuthorization(Scopes.MessagesRead);
    }

    private static async Task<IResult> Send(
        SendMessageRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal user,
        HttpResponse response,
        SendMessageHandler handler,
        CancellationToken cancellationToken)
    {
        var outcome = await handler.HandleAsync(user.TenantId(), request, idempotencyKey, cancellationToken);
        return ToResult(outcome, response, accepted => TypedResults.Accepted(
            $"/v1/messages/{accepted.Message.Id}", MessageResponse.Create(accepted.Message)));
    }

    private static async Task<IResult> SendBulk(
        SendBulkRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal user,
        HttpResponse response,
        SendMessageHandler handler,
        CancellationToken cancellationToken)
    {
        var outcome = await handler.HandleBulkAsync(user.TenantId(), request, idempotencyKey, cancellationToken);
        return ToResult(outcome, response, accepted => TypedResults.Accepted(
            (string?)null, new BulkResponse([.. accepted.Messages.Select(MessageResponse.Create)])));
    }

    private static IResult ToResult(SendOutcome outcome, HttpResponse response, Func<SendAccepted, IResult> accepted)
    {
        switch (outcome)
        {
            case SendAccepted ok:
                if (ok.Replayed)
                {
                    response.Headers["Idempotent-Replayed"] = "true";
                }

                return accepted(ok);
            case SendInvalid invalid:
                return TypedResults.ValidationProblem(invalid.Errors);
            case SendIdempotencyConflict:
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Idempotency-Key was already used with a different request.");
            case SendSuppressed suppressed:
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Some recipients are on the suppression list.",
                    detail: "Nothing was accepted. Remove them, or delete the suppression if it is wrong.",
                    extensions: new Dictionary<string, object?> { ["suppressed"] = suppressed.Addresses });
            case SendQuotaExceeded quota:
                response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(quota.RetryAfter.TotalSeconds))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Daily recipient quota exceeded.",
                    detail: "The quota resets at 00:00 UTC. See Retry-After.");
            case SendPaused paused:
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Sending is paused for this tenant.",
                    detail: paused.Reason);
            default:
                throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.");
        }
    }

    private static async Task<Results<Ok<MessageDetailsResponse>, ProblemHttpResult>> Get(
        Guid id, ClaimsPrincipal user, IMessageRepository messages, CancellationToken cancellationToken)
    {
        // Scoped by tenant: another tenant's id is indistinguishable from a missing one.
        var message = await messages.FindAsync(user.TenantId(), id, cancellationToken);
        return message is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Message not found.")
            : TypedResults.Ok(MessageDetailsResponse.Create(message));
    }

    private static async Task<Results<Ok<Explanation>, ProblemHttpResult>> Explain(
        Guid id, ClaimsPrincipal user, IMessageRepository messages, CancellationToken cancellationToken)
    {
        var message = await messages.FindAsync(user.TenantId(), id, cancellationToken);
        return message is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Message not found.")
            : TypedResults.Ok(MessageExplainer.Explain(message));
    }

    private static async Task<Results<Ok<MessagePage>, ValidationProblem>> List(
        ClaimsPrincipal user, IMessageRepository messages, CancellationToken cancellationToken,
        Guid? before = null, int limit = 50)
    {
        if (limit is < 1 or > 100)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["limit"] = ["Must be 1-100."] });
        }

        var items = await messages.ListAsync(user.TenantId(), before, limit, cancellationToken);
        var next = items.Count == limit ? items[^1].Id : (Guid?)null;
        return TypedResults.Ok(new MessagePage([.. items.Select(MessageResponse.Create)], next));
    }
}

internal sealed record MessageResponse(
    Guid Id, MessageStatus Status, string From, IReadOnlyList<string> To, IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc, string Subject, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static MessageResponse Create(Message m) =>
        new(m.Id, m.Status, m.From, m.To, m.Cc, m.Bcc, m.Subject, m.CreatedAt, m.UpdatedAt);
}

internal sealed record MessageEventResponse(MessageStatus? From, MessageStatus To, DateTimeOffset At, string? Detail);

internal sealed record MessageDetailsResponse(MessageResponse Message, IReadOnlyList<MessageEventResponse> Events)
{
    public static MessageDetailsResponse Create(Message m) =>
        new(MessageResponse.Create(m), [.. m.Events.Select(e => new MessageEventResponse(e.FromStatus, e.ToStatus, e.OccurredAt, e.Detail))]);
}

internal sealed record MessagePage(IReadOnlyList<MessageResponse> Items, Guid? NextBefore);

internal sealed record BulkResponse(IReadOnlyList<MessageResponse> Messages);
