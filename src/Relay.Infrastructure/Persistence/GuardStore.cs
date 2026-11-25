using Microsoft.EntityFrameworkCore;
using Relay.Domain;
using Relay.Guard;

namespace Relay.Infrastructure.Persistence;

internal sealed class GuardStore(RelayDbContext db) : IGuardStore
{
    public async Task AddRunAsync(AgentRun run, CancellationToken cancellationToken)
    {
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<AgentRun?> FindRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken) =>
        db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == runId, cancellationToken);

    public async Task<GuardFacts> GetFactsAsync(
        AgentRun run, IReadOnlyList<string> recipients, string contentHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var recips = recipients.ToArray();
        var hourAgo = now.AddHours(-1);
        var dayAgo = now.AddDays(-1);
        var dupSince = now.AddHours(-run.Policy.DuplicateWindowHours);

        var runRecipients = await db.Database.SqlQuery<string>($"""
            SELECT DISTINCT lower(r) AS "Value"
            FROM messages m, unnest(m."to" || m.cc || m.bcc) AS r
            WHERE m.agent_run_id = {run.Id} AND m.status <> 'Rejected'
            """).ToListAsync(cancellationToken);

        var sendsLastHour = await db.Messages.CountAsync(
            m => m.AgentRunId == run.Id && m.CreatedAt >= hourAgo && m.Status != MessageStatus.Rejected, cancellationToken);

        var perRecipient = await db.Database.SqlQuery<string>($"""
            SELECT lower(r) || '|' || count(*) AS "Value"
            FROM messages m, unnest(m."to" || m.cc || m.bcc) AS r
            WHERE m.tenant_id = {run.TenantId} AND m.created_at >= {dayAgo} AND m.status <> 'Rejected'
              AND lower(r) = ANY({recips})
            GROUP BY lower(r)
            """).ToListAsync(cancellationToken);

        var duplicate = await db.Database.SqlQuery<Guid>($"""
            SELECT m.id AS "Value"
            FROM messages m
            WHERE m.tenant_id = {run.TenantId} AND m.content_hash = {contentHash}
              AND m.created_at >= {dupSince} AND m.status <> 'Rejected'
              AND EXISTS (SELECT 1 FROM unnest(m."to" || m.cc || m.bcc) AS r WHERE lower(r) = ANY({recips}))
            ORDER BY m.created_at
            LIMIT 1
            """).ToListAsync(cancellationToken);

        return new GuardFacts(
            runRecipients.ToHashSet(StringComparer.Ordinal),
            sendsLastHour,
            perRecipient.Select(v => v.Split('|')).ToDictionary(p => p[0], p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal),
            duplicate.Count == 0 ? null : duplicate[0]);
    }

    public async Task AddAuditAsync(GuardAuditEntry entry, CancellationToken cancellationToken)
    {
        db.GuardAudit.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddApprovalAsync(ApprovalRequest approval, GuardAuditEntry audit, CancellationToken cancellationToken)
    {
        db.Approvals.Add(approval);
        db.GuardAudit.Add(audit);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<ApprovalRequest?> FindApprovalAsync(Guid tenantId, Guid approvalId, CancellationToken cancellationToken) =>
        db.Approvals.SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == approvalId, cancellationToken);

    public async Task<IReadOnlyList<ApprovalRequest>> ListApprovalsAsync(
        Guid tenantId, ApprovalStatus? status, CancellationToken cancellationToken) =>
        await db.Approvals.AsNoTracking()
            .Where(a => a.TenantId == tenantId && (status == null || a.Status == status))
            .OrderByDescending(a => a.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ApprovalRequest>> FindOverdueAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Approvals
            .Where(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending && a.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

    public async Task ApplyDecisionAsync(ApprovalRequest approval, bool release, GuardAuditEntry audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(audit);
        var message = await db.Messages.Include(m => m.Events).SingleAsync(m => m.Id == approval.MessageId, cancellationToken);
        var at = audit.At;
        if (release)
        {
            message.Transition(MessageStatus.Accepted, at, $"Approved by {approval.DecidedBy}");
            db.Outbox.Add(new OutboxMessage
            {
                Id = Guid.CreateVersion7(at),
                MessageId = message.Id,
                TenantId = message.TenantId,
                CreatedAt = at,
                AvailableAt = at,
            });
        }
        else if (message.Status == MessageStatus.Held)
        {
            message.Transition(MessageStatus.Rejected, at, approval.Status == ApprovalStatus.Expired
                ? "Approval expired without a decision"
                : $"Rejected by {approval.DecidedBy}");
        }

        db.GuardAudit.Add(audit);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GuardAuditEntry>> ListAuditAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken) =>
        await db.GuardAudit.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.RunId == runId)
            .OrderBy(a => a.At)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Message>> ListRunMessagesAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken) =>
        await db.Messages.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.AgentRunId == runId)
            .OrderByDescending(m => m.Id)
            .Take(200)
            .ToListAsync(cancellationToken);
}
