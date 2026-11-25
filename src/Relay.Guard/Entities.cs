namespace Relay.Guard;

/// <summary>One agent task with its send envelope. Its token can only send within it.</summary>
public sealed class AgentRun
{
    private AgentRun()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public IReadOnlyList<string> TrustedRecipients { get; private set; } = [];
    public GuardPolicy Policy { get; private set; } = new();
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public static AgentRun Create(
        Guid id, Guid tenantId, string name, IEnumerable<string> trustedRecipients, GuardPolicy policy, DateTimeOffset now, TimeSpan ttl) =>
        new()
        {
            Id = id,
            TenantId = tenantId,
            Name = name,
            TrustedRecipients = [.. trustedRecipients.Select(Relay.Application.EmailAddress.Normalize).Distinct(StringComparer.Ordinal)],
            Policy = policy,
            CreatedAt = now,
            ExpiresAt = now + ttl,
        };

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}

public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
}

/// <summary>A held message waiting for a human. Created by the Guard or by the agent asking.</summary>
public sealed class ApprovalRequest
{
    private ApprovalRequest()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RunId { get; private set; }
    public Guid MessageId { get; private set; }
    public ApprovalStatus Status { get; private set; }
    public int RiskScore { get; private set; }
    public IReadOnlyList<string> Reasons { get; private set; } = [];

    /// <summary>"guard" when the Guard held it, "agent" when the agent asked.</summary>
    public string Source { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? DecidedBy { get; private set; }
    public string? Note { get; private set; }

    public static ApprovalRequest Create(
        Guid id, AgentRun run, Guid messageId, int riskScore, IEnumerable<string> reasons, string source, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new()
        {
            Id = id,
            TenantId = run.TenantId,
            RunId = run.Id,
            MessageId = messageId,
            Status = ApprovalStatus.Pending,
            RiskScore = riskScore,
            Reasons = [.. reasons],
            Source = source,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(run.Policy.ApprovalTtlMinutes),
        };
    }

    public bool Decide(ApprovalStatus status, string decidedBy, string? note, DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Pending)
        {
            return false;
        }

        Status = now >= ExpiresAt ? ApprovalStatus.Expired : status;
        DecidedAt = now;
        DecidedBy = Status == ApprovalStatus.Expired ? "expiry" : decidedBy;
        Note = note;
        return Status == status;
    }
}

/// <summary>Append-only record of every Guard decision and every approval decision.</summary>
public sealed class GuardAuditEntry
{
    private GuardAuditEntry()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RunId { get; private set; }
    public Guid? MessageId { get; private set; }
    public Guid? ApprovalId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public int RiskScore { get; private set; }
    public IReadOnlyList<string> Reasons { get; private set; } = [];
    public string Actor { get; private set; } = string.Empty;
    public DateTimeOffset At { get; private set; }

    public static GuardAuditEntry Create(
        AgentRun run, string action, int riskScore, IEnumerable<string> reasons, string actor, DateTimeOffset at,
        Guid? messageId = null, Guid? approvalId = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new()
        {
            Id = Guid.CreateVersion7(at),
            TenantId = run.TenantId,
            RunId = run.Id,
            MessageId = messageId,
            ApprovalId = approvalId,
            Action = action,
            RiskScore = riskScore,
            Reasons = [.. reasons],
            Actor = actor,
            At = at,
        };
    }
}
