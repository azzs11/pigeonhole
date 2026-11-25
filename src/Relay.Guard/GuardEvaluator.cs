namespace Relay.Guard;

public enum GuardAction
{
    Allow,
    Hold,
    Block,
    Duplicate,
}

public enum Provenance
{
    /// <summary>Named on the run by the human or app that started it.</summary>
    Trusted,

    /// <summary>At a domain the run's policy allows.</summary>
    AllowedDomain,

    /// <summary>Came from somewhere else, such as text the agent read.</summary>
    Untrusted,
}

public sealed record RecipientVerdict(string Address, Provenance Provenance);

/// <summary>What the database knows about this run and these recipients right now.</summary>
public sealed record GuardFacts(
    IReadOnlySet<string> RunRecipients,
    int SendsLastHour,
    IReadOnlyDictionary<string, int> SendsTodayByRecipient,
    Guid? DuplicateOf);

public sealed record GuardInput(
    IReadOnlyList<string> Recipients, string? Subject, string? Text, string? Html, IReadOnlySet<string> TrustedRecipients);

public sealed record GuardDecision(
    GuardAction Action,
    int RiskScore,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<RecipientVerdict> Recipients,
    Guid? DuplicateOf);

/// <summary>The Guard's rules as a pure function: same input, same decision. All I/O is in the facts.</summary>
public static class GuardEvaluator
{
    public static GuardDecision Evaluate(GuardInput input, GuardFacts facts, GuardPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(policy);
        var checks = policy.Checks;
        var recipients = input.Recipients.Distinct(StringComparer.Ordinal).ToList();
        var verdicts = recipients.Select(r => new RecipientVerdict(r, ProvenanceOf(r, input.TrustedRecipients, policy))).ToList();
        var reasons = new List<string>();

        if (checks.Caps)
        {
            var total = facts.RunRecipients.Union(recipients).Count();
            if (recipients.Count > policy.MaxRecipientsPerMessage)
            {
                reasons.Add($"{recipients.Count} recipients exceeds the per-message cap of {policy.MaxRecipientsPerMessage}.");
            }

            if (total > policy.MaxRecipientsPerRun)
            {
                reasons.Add($"This run would reach {total} distinct recipients; the cap is {policy.MaxRecipientsPerRun}.");
            }

            if (facts.SendsLastHour + 1 > policy.MaxSendsPerHour)
            {
                reasons.Add($"The run already sent {facts.SendsLastHour} messages in the last hour; the cap is {policy.MaxSendsPerHour}.");
            }

            reasons.AddRange(recipients
                .Where(r => facts.SendsTodayByRecipient.GetValueOrDefault(r) + 1 > policy.MaxSendsPerRecipientPerDay)
                .Select(r => $"{r} already received {facts.SendsTodayByRecipient[r]} messages today; the cap is {policy.MaxSendsPerRecipientPerDay}."));

            if (reasons.Count > 0)
            {
                return new GuardDecision(GuardAction.Block, 100, reasons, [], verdicts, null);
            }
        }

        if (checks.Duplicates && facts.DuplicateOf is { } original)
        {
            return new GuardDecision(GuardAction.Duplicate, 0,
                ["A near-identical message to the same recipient was already sent; returning it instead of sending again."],
                [], verdicts, original);
        }

        var score = 0;
        if (checks.Provenance)
        {
            var untrusted = verdicts.Where(v => v.Provenance == Provenance.Untrusted).ToList();
            if (untrusted.Count > 0)
            {
                score += Math.Min(80, 50 + (10 * (untrusted.Count - 1)));
                reasons.AddRange(untrusted.Select(v =>
                    $"{v.Address} was not named on this run and is not at an allowed domain; it may have come from content the agent read."));
            }
        }

        IReadOnlyList<Finding> findings = [];
        if (checks.SecretScan)
        {
            findings = SecretScanner.Scan(input.Subject, input.Text, input.Html);
            var worst = findings.Count == 0 ? (FindingSeverity?)null : findings.Max(f => f.Severity);
            score += worst switch
            {
                FindingSeverity.High => 60,
                FindingSeverity.Medium => 45,
                FindingSeverity.Low => 25,
                _ => 0,
            };
            reasons.AddRange(findings.Select(f => $"Content contains what looks like a {f.Kind} ({f.Sample})."));
        }

        score = Math.Min(100, score);
        var action = score >= policy.BlockThreshold ? GuardAction.Block
            : score >= policy.HoldThreshold ? GuardAction.Hold
            : GuardAction.Allow;
        return new GuardDecision(action, score, reasons, findings, verdicts, null);
    }

    public static Provenance ProvenanceOf(string address, IReadOnlySet<string> trusted, GuardPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(trusted);
        ArgumentNullException.ThrowIfNull(policy);
        if (trusted.Contains(address))
        {
            return Provenance.Trusted;
        }

        var domain = address[(address.LastIndexOf('@') + 1)..];
        return policy.AllowedDomains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase))
            ? Provenance.AllowedDomain
            : Provenance.Untrusted;
    }
}
