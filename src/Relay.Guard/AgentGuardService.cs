using Relay.Application;
using Relay.Domain;

namespace Relay.Guard;

public interface IGuardStore
{
    Task AddRunAsync(AgentRun run, CancellationToken cancellationToken);

    Task<AgentRun?> FindRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken);

    Task<GuardFacts> GetFactsAsync(
        AgentRun run, IReadOnlyList<string> recipients, string contentHash, DateTimeOffset now, CancellationToken cancellationToken);

    Task AddAuditAsync(GuardAuditEntry entry, CancellationToken cancellationToken);

    Task AddApprovalAsync(ApprovalRequest approval, GuardAuditEntry audit, CancellationToken cancellationToken);

    Task<ApprovalRequest?> FindApprovalAsync(Guid tenantId, Guid approvalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApprovalRequest>> ListApprovalsAsync(Guid tenantId, ApprovalStatus? status, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApprovalRequest>> FindOverdueAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: saves the decided approval and its audit entry, and moves the held message
    /// to Accepted (with a new outbox row) when <paramref name="release"/> is true, otherwise to Rejected.
    /// </summary>
    Task ApplyDecisionAsync(ApprovalRequest approval, bool release, GuardAuditEntry audit, CancellationToken cancellationToken);

    Task<IReadOnlyList<GuardAuditEntry>> ListAuditAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Message>> ListRunMessagesAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken);
}

public enum AgentSendStatus
{
    Sent,
    Held,
    Blocked,
    Duplicate,
    DryRun,
    Invalid,
    Refused,
    NotFound,
}

public sealed record SendPreview(
    GuardAction WouldBe, string From, IReadOnlyList<string> To, IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc,
    string Subject, string? Text, string? Html);

public sealed record AgentSendResult(
    AgentSendStatus Status,
    Guid? MessageId = null,
    Guid? ApprovalId = null,
    int RiskScore = 0,
    IReadOnlyList<string>? Reasons = null,
    IReadOnlyList<Finding>? Findings = null,
    IReadOnlyList<RecipientVerdict>? Recipients = null,
    double GuardMilliseconds = 0,
    IDictionary<string, string[]>? Errors = null,
    SendPreview? Preview = null);

public sealed record CreateRunRequest
{
    public string? Name { get; init; }
    public IReadOnlyList<string>? TrustedRecipients { get; init; }
    public GuardPolicy? Policy { get; init; }
    public int TtlMinutes { get; init; } = 120;
}

public sealed record CreatedRun(AgentRun Run, string Token);

public enum DecisionResult
{
    Done,
    NotFound,
    AlreadyDecided,
    Expired,
}

/// <summary>The Agent Send Guard: evaluates every agent send against the run's envelope, then sends, holds or refuses it.</summary>
public sealed class AgentGuardService(
    IGuardStore store, SendMessageHandler sender, ApiKeyService keys, MessagingOptions options, TimeProvider time)
{
    public async Task<(CreatedRun? Run, IDictionary<string, string[]>? Errors)> CreateRunAsync(
        Guid tenantId, CreateRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trusted = request.TrustedRecipients ?? [];
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var bad = trusted.Where(a => !EmailAddress.IsValid(a)).ToArray();
        if (bad.Length > 0)
        {
            errors["trustedRecipients"] = [$"{bad.Length} entries are not valid email addresses."];
        }

        if (request.TtlMinutes is < 1 or > 1440)
        {
            errors["ttlMinutes"] = ["Must be 1-1440."];
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var now = time.GetUtcNow();
        var run = AgentRun.Create(Guid.CreateVersion7(now), tenantId, request.Name ?? "agent run", trusted,
            request.Policy ?? new GuardPolicy(), now, TimeSpan.FromMinutes(request.TtlMinutes));
        await store.AddRunAsync(run, cancellationToken);
        var token = await keys.IssueRunTokenAsync(tenantId, run.Id, cancellationToken);
        await store.AddAuditAsync(GuardAuditEntry.Create(run, "RunCreated", 0,
            [$"{run.TrustedRecipients.Count} trusted recipients", $"allowed domains: {string.Join(", ", run.Policy.AllowedDomains)}"],
            "operator", now), cancellationToken);
        return (new CreatedRun(run, token.Plaintext), null);
    }

    public async Task<AgentSendResult> SendAsync(
        Guid tenantId, Guid runId, SendMessageRequest request, bool dryRun, CancellationToken cancellationToken)
    {
        var (run, early) = await LoadAsync(tenantId, runId, request, cancellationToken);
        if (early is not null)
        {
            return early;
        }

        var (decision, hash, ms) = await EvaluateAsync(run!, request, cancellationToken);
        var result = new AgentSendResult(AgentSendStatus.Blocked, null, null, decision.RiskScore, decision.Reasons,
            decision.Findings, decision.Recipients, ms);
        var now = time.GetUtcNow();

        if (dryRun)
        {
            return result with
            {
                Status = AgentSendStatus.DryRun,
                MessageId = decision.DuplicateOf,
                Preview = new SendPreview(decision.Action, request.From ?? options.DefaultFrom, request.To ?? [],
                    request.Cc ?? [], request.Bcc ?? [], request.Subject!, request.Text, request.Html),
            };
        }

        switch (decision.Action)
        {
            case GuardAction.Block:
                await store.AddAuditAsync(GuardAuditEntry.Create(run!, "Blocked", decision.RiskScore, decision.Reasons, "guard", now), cancellationToken);
                return result;
            case GuardAction.Duplicate:
                await store.AddAuditAsync(GuardAuditEntry.Create(run!, "Duplicate", 0, decision.Reasons, "guard", now, decision.DuplicateOf), cancellationToken);
                return result with { Status = AgentSendStatus.Duplicate, MessageId = decision.DuplicateOf };
            case GuardAction.Hold:
                return await HoldAsync(run!, request, hash, decision.RiskScore, decision.Reasons, "guard", result, cancellationToken);
            default:
                var outcome = await sender.HandleAsync(tenantId, request, null, new SendContext(run!.Id, hash, null), cancellationToken);
                if (outcome is not SendAccepted accepted)
                {
                    return result with { Status = AgentSendStatus.Refused, Reasons = [Describe(outcome)] };
                }

                await store.AddAuditAsync(GuardAuditEntry.Create(run, "Allowed", decision.RiskScore, decision.Reasons, "guard", now,
                    accepted.Message.Id), cancellationToken);
                return result with { Status = AgentSendStatus.Sent, MessageId = accepted.Message.Id };
        }
    }

    /// <summary>The agent asks a human to approve a send. Hard caps still apply; everything else is held, never sent directly.</summary>
    public async Task<AgentSendResult> RequestApprovalAsync(
        Guid tenantId, Guid runId, SendMessageRequest request, string? reason, CancellationToken cancellationToken)
    {
        var (run, early) = await LoadAsync(tenantId, runId, request, cancellationToken);
        if (early is not null)
        {
            return early;
        }

        var (decision, hash, ms) = await EvaluateAsync(run!, request, cancellationToken);
        var result = new AgentSendResult(AgentSendStatus.Blocked, null, null, decision.RiskScore, decision.Reasons,
            decision.Findings, decision.Recipients, ms);
        if (decision.Action == GuardAction.Block && decision.RiskScore == 100 && decision.Findings.Count == 0)
        {
            await store.AddAuditAsync(GuardAuditEntry.Create(run!, "Blocked", 100, decision.Reasons, "guard", time.GetUtcNow()), cancellationToken);
            return result;
        }

        string[] reasons = [$"The agent asked for approval: {reason ?? "no reason given"}", .. decision.Reasons];
        return await HoldAsync(run!, request, hash, decision.RiskScore, reasons, "agent", result, cancellationToken);
    }

    public async Task<IReadOnlyList<ApprovalRequest>> ListApprovalsAsync(Guid tenantId, ApprovalStatus? status, CancellationToken cancellationToken)
    {
        await ExpireOverdueAsync(tenantId, cancellationToken);
        return await store.ListApprovalsAsync(tenantId, status, cancellationToken);
    }

    public async Task<DecisionResult> DecideAsync(
        Guid tenantId, Guid approvalId, bool approve, string actor, string? note, CancellationToken cancellationToken)
    {
        var approval = await store.FindApprovalAsync(tenantId, approvalId, cancellationToken);
        if (approval is null)
        {
            return DecisionResult.NotFound;
        }

        if (approval.Status != ApprovalStatus.Pending)
        {
            return DecisionResult.AlreadyDecided;
        }

        var run = await store.FindRunAsync(tenantId, approval.RunId, cancellationToken);
        var now = time.GetUtcNow();
        var wanted = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        var applied = approval.Decide(wanted, actor, note, now);
        await store.ApplyDecisionAsync(approval, release: applied && approve,
            GuardAuditEntry.Create(run!, approval.Status.ToString(), approval.RiskScore, [note ?? string.Empty], actor, now,
                approval.MessageId, approval.Id), cancellationToken);
        return applied ? DecisionResult.Done : DecisionResult.Expired;
    }

    public Task<IReadOnlyList<GuardAuditEntry>> AuditAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken) =>
        store.ListAuditAsync(tenantId, runId, cancellationToken);

    public Task<IReadOnlyList<Message>> ListSentAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken) =>
        store.ListRunMessagesAsync(tenantId, runId, cancellationToken);

    private async Task ExpireOverdueAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        foreach (var approval in await store.FindOverdueAsync(tenantId, now, cancellationToken))
        {
            var run = await store.FindRunAsync(tenantId, approval.RunId, cancellationToken);
            approval.Decide(ApprovalStatus.Expired, "expiry", "No decision before the approval deadline.", now);
            await store.ApplyDecisionAsync(approval, release: false,
                GuardAuditEntry.Create(run!, "Expired", approval.RiskScore, ["No decision before the deadline."], "expiry", now,
                    approval.MessageId, approval.Id), cancellationToken);
        }
    }

    private async Task<(AgentRun? Run, AgentSendResult? Early)> LoadAsync(
        Guid tenantId, Guid runId, SendMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = await store.FindRunAsync(tenantId, runId, cancellationToken);
        if (run is null)
        {
            return (null, new AgentSendResult(AgentSendStatus.NotFound));
        }

        if (run.IsExpired(time.GetUtcNow()))
        {
            return (run, new AgentSendResult(AgentSendStatus.Blocked, Reasons: ["This agent run has expired; start a new run."]));
        }

        var errors = MessageValidator.Validate(request, options);
        return errors.Count > 0 ? (run, new AgentSendResult(AgentSendStatus.Invalid, Errors: errors)) : (run, null);
    }

    private async Task<(GuardDecision Decision, string Hash, double Milliseconds)> EvaluateAsync(
        AgentRun run, SendMessageRequest request, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var recipients = (request.To ?? []).Concat(request.Cc ?? []).Concat(request.Bcc ?? [])
            .Select(EmailAddress.Normalize).Distinct(StringComparer.Ordinal).ToList();
        var hash = ContentFingerprint.Compute(request.Subject, request.Text, request.Html);
        var facts = await store.GetFactsAsync(run, recipients, hash, time.GetUtcNow(), cancellationToken);
        var decision = GuardEvaluator.Evaluate(
            new GuardInput(recipients, request.Subject, request.Text, request.Html, run.TrustedRecipients.ToHashSet(StringComparer.Ordinal)),
            facts, run.Policy);
        return (decision, hash, time.GetElapsedTime(started).TotalMilliseconds);
    }

    private async Task<AgentSendResult> HoldAsync(
        AgentRun run, SendMessageRequest request, string hash, int score, IReadOnlyList<string> reasons, string source,
        AgentSendResult result, CancellationToken cancellationToken)
    {
        var outcome = await sender.HandleAsync(run.TenantId, request, null,
            new SendContext(run.Id, hash, $"Held for approval (risk {score})"), cancellationToken);
        if (outcome is not SendAccepted accepted)
        {
            return result with { Status = AgentSendStatus.Refused, Reasons = [Describe(outcome)] };
        }

        var now = time.GetUtcNow();
        var approval = ApprovalRequest.Create(Guid.CreateVersion7(now), run, accepted.Message.Id, score, reasons, source, now);
        await store.AddApprovalAsync(approval,
            GuardAuditEntry.Create(run, "Held", score, reasons, source, now, accepted.Message.Id, approval.Id), cancellationToken);
        return result with { Status = AgentSendStatus.Held, MessageId = accepted.Message.Id, ApprovalId = approval.Id };
    }

    private static string Describe(SendOutcome outcome) => outcome switch
    {
        SendSuppressed s => $"Recipients are suppressed: {string.Join(", ", s.Addresses)}.",
        SendQuotaExceeded => "The tenant's daily quota is used up.",
        SendPaused p => $"Sending is paused: {p.Reason}",
        SendInvalid => "The message is invalid.",
        _ => outcome.GetType().Name,
    };
}
