using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Relay.Api.Auth;
using Relay.Application;
using Relay.Guard;

namespace Relay.Api.Endpoints;

/// <summary>
/// Agent Send Guard. Operators (agents:manage) create runs and decide approvals; agents hold a
/// run token (agent:send) that can only send through the Guard inside that one run.
/// </summary>
internal static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var runs = app.MapGroup("/v1/agent-runs").WithTags("Agent runs").RequireAuthorization(Scopes.AgentsManage);
        runs.MapPost("/", CreateRun);
        runs.MapGet("/{runId:guid}/audit", Audit);

        var approvals = app.MapGroup("/v1/approvals").WithTags("Approvals").RequireAuthorization(Scopes.AgentsManage);
        approvals.MapGet("/", ListApprovals);
        approvals.MapPost("/{id:guid}/approve", (Guid id, DecisionRequest? body, ClaimsPrincipal user, AgentGuardService guard, CancellationToken ct) =>
            Decide(id, approve: true, body, user, guard, ct));
        approvals.MapPost("/{id:guid}/reject", (Guid id, DecisionRequest? body, ClaimsPrincipal user, AgentGuardService guard, CancellationToken ct) =>
            Decide(id, approve: false, body, user, guard, ct));

        var agent = app.MapGroup("/v1/agent").WithTags("Agent (run token)").RequireAuthorization(Scopes.AgentSend);
        agent.MapPost("/messages", Send);
        agent.MapGet("/messages", ListSent);
        agent.MapGet("/messages/{id:guid}", Status);
        agent.MapPost("/approvals", RequestApproval);
    }

    private static async Task<IResult> CreateRun(
        CreateRunRequest request, ClaimsPrincipal user, AgentGuardService guard, CancellationToken cancellationToken)
    {
        var (created, errors) = await guard.CreateRunAsync(user.TenantId(), request, cancellationToken);
        if (created is null)
        {
            return TypedResults.ValidationProblem(errors!);
        }

        return TypedResults.Created($"/v1/agent-runs/{created.Run.Id}/audit", new
        {
            runId = created.Run.Id,
            token = created.Token,
            note = "The token is shown once. Give it to the agent's MCP server (RELAY_RUN_TOKEN); it can only send within this run.",
            created.Run.ExpiresAt,
            created.Run.TrustedRecipients,
            created.Run.Policy,
        });
    }

    private static async Task<Ok<IReadOnlyList<GuardAuditEntry>>> Audit(
        Guid runId, ClaimsPrincipal user, AgentGuardService guard, CancellationToken cancellationToken) =>
        TypedResults.Ok(await guard.AuditAsync(user.TenantId(), runId, cancellationToken));

    private static async Task<Ok<IReadOnlyList<ApprovalRequest>>> ListApprovals(
        ClaimsPrincipal user, AgentGuardService guard, CancellationToken cancellationToken, ApprovalStatus? status = ApprovalStatus.Pending) =>
        TypedResults.Ok(await guard.ListApprovalsAsync(user.TenantId(), status, cancellationToken));

    private static async Task<IResult> Decide(
        Guid id, bool approve, DecisionRequest? body, ClaimsPrincipal user, AgentGuardService guard, CancellationToken cancellationToken)
    {
        var actor = $"key:{user.FindFirstValue(ClaimTypes.NameIdentifier)}";
        return await guard.DecideAsync(user.TenantId(), id, approve, actor, body?.Note, cancellationToken) switch
        {
            DecisionResult.Done => TypedResults.Ok(new { id, decision = approve ? "Approved" : "Rejected" }),
            DecisionResult.NotFound => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Approval not found."),
            DecisionResult.Expired => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "The approval expired before the decision."),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "This approval was already decided."),
        };
    }

    private static async Task<IResult> Send(
        SendMessageRequest request, ClaimsPrincipal user, AgentGuardService guard, CancellationToken cancellationToken, bool dryRun = false) =>
        ToResult(await guard.SendAsync(user.TenantId(), user.RunId()!.Value, request, dryRun, cancellationToken));

    private static async Task<IResult> RequestApproval(
        ApprovalAsk request, ClaimsPrincipal user, AgentGuardService guard, CancellationToken cancellationToken) =>
        ToResult(await guard.RequestApprovalAsync(user.TenantId(), user.RunId()!.Value, request.Message ?? new(), request.Reason, cancellationToken));

    private static async Task<Ok<IReadOnlyList<MessageResponse>>> ListSent(
        ClaimsPrincipal user, AgentGuardService guard, CancellationToken cancellationToken) =>
        TypedResults.Ok<IReadOnlyList<MessageResponse>>(
            [.. (await guard.ListSentAsync(user.TenantId(), user.RunId()!.Value, cancellationToken)).Select(MessageResponse.Create)]);

    private static async Task<IResult> Status(
        Guid id, ClaimsPrincipal user, IMessageRepository messages, CancellationToken cancellationToken)
    {
        var message = await messages.FindAsync(user.TenantId(), id, cancellationToken);
        return message is null || message.AgentRunId != user.RunId()
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No message with that id in this run.")
            : TypedResults.Ok(new { message = MessageResponse.Create(message), explanation = MessageExplainer.Explain(message) });
    }

    private static IResult ToResult(AgentSendResult result) => result.Status switch
    {
        AgentSendStatus.Sent or AgentSendStatus.Held => TypedResults.Accepted((string?)null, result),
        AgentSendStatus.Duplicate or AgentSendStatus.DryRun => TypedResults.Ok(result),
        AgentSendStatus.Invalid => TypedResults.ValidationProblem(result.Errors!),
        AgentSendStatus.NotFound => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Agent run not found."),
        AgentSendStatus.Refused => TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "The message could not be accepted.", detail: string.Join(" ", result.Reasons ?? [])),
        _ => TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Blocked by the Agent Send Guard.",
            detail: string.Join(" ", result.Reasons ?? []), extensions: new Dictionary<string, object?> { ["guard"] = result }),
    };
}

internal sealed record DecisionRequest(string? Note);

internal sealed record ApprovalAsk(SendMessageRequest? Message, string? Reason);
