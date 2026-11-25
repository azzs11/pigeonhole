using Microsoft.Extensions.DependencyInjection;
using Relay.Application;
using Relay.Guard;

namespace AgentMailBench;

internal sealed record ScenarioResult(Scenario Scenario, AgentSendStatus[] Decisions, double[] GuardMilliseconds)
{
    /// <summary>
    /// Retry storms and blasts: no more than <see cref="Scenario.MaxSends"/> emails actually went out ("must dedupe", "must cap").
    /// Exfiltration: no call that must not send was sent. Held counts as stopped: a human decides.
    /// </summary>
    public bool Stopped => Scenario.MaxSends is { } max
        ? Decisions.Count(d => d == AgentSendStatus.Sent) <= max
        : Scenario.Calls.Zip(Decisions).All(x => x.First.ShouldSend || x.Second != AgentSendStatus.Sent);
}

internal sealed record ConfigSummary(
    string Config, int Scenarios, int AttackScenarios, int AttacksStopped, double BlockRate,
    int BenignCalls, int BenignBlocked, int BenignHeld, double FalseBlockRate, double ApprovalShare,
    double GuardP50Ms, double GuardP99Ms, IReadOnlyDictionary<string, string> StoppedByGroup, IReadOnlyList<string> NotStopped);

internal static class Runner
{
    public static readonly (string Name, GuardChecks Checks)[] Configs =
    [
        ("no guard", GuardChecks.None),
        ("caps only", GuardChecks.CapsOnly),
        ("caps + provenance", GuardChecks.CapsAndProvenance),
        ("full guard", GuardChecks.Full),
    ];

    /// <summary>Runs every scenario in its own tenant and run, so caps and duplicates never leak between scenarios.</summary>
    public static async Task<ConfigSummary> RunAsync(
        IServiceProvider services, string config, GuardChecks checks, IReadOnlyList<Scenario> scenarios, CancellationToken cancellationToken)
    {
        var results = new List<ScenarioResult>();
        using var gate = new SemaphoreSlim(4);
        var tasks = scenarios.Select(async s =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var r = await RunScenarioAsync(services, checks, s, cancellationToken).ConfigureAwait(false);
                lock (results)
                {
                    results.Add(r);
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return Summarize(config, results);
    }

    private static async Task<ScenarioResult> RunScenarioAsync(
        IServiceProvider services, GuardChecks checks, Scenario scenario, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var tenant = (await sp.GetRequiredService<ApiKeyService>()
            .IssueAsync($"amb-{Guid.NewGuid():N}", "bench", Scopes.All, cancellationToken).ConfigureAwait(false)).Key.TenantId;
        var guard = sp.GetRequiredService<AgentGuardService>();
        var (created, _) = await guard.CreateRunAsync(tenant, new CreateRunRequest
        {
            Name = scenario.Id,
            TrustedRecipients = scenario.Trusted,
            Policy = new GuardPolicy { AllowedDomains = scenario.AllowedDomains, Checks = checks },
        }, cancellationToken).ConfigureAwait(false);

        var decisions = new AgentSendStatus[scenario.Calls.Length];
        var latency = new double[scenario.Calls.Length];
        for (var i = 0; i < scenario.Calls.Length; i++)
        {
            var call = scenario.Calls[i];
            var result = await guard.SendAsync(tenant, created!.Run.Id,
                new SendMessageRequest { To = call.To, Cc = call.Cc, Bcc = call.Bcc, Subject = call.Subject, Text = call.Text },
                dryRun: false, cancellationToken).ConfigureAwait(false);
            decisions[i] = result.Status;
            latency[i] = result.GuardMilliseconds;
        }

        return new ScenarioResult(scenario, decisions, latency);
    }

    private static ConfigSummary Summarize(string config, List<ScenarioResult> results)
    {
        var attacks = results.Where(r => r.Scenario.Group != ScenarioGroup.Benign).ToList();
        var benignCalls = results.Where(r => r.Scenario.Group == ScenarioGroup.Benign).SelectMany(r => r.Decisions).ToList();
        var latencies = results.SelectMany(r => r.GuardMilliseconds).Order().ToArray();
        double Percentile(double p) => latencies.Length == 0 ? 0 : latencies[(int)Math.Min(latencies.Length - 1, Math.Floor(p * latencies.Length))];
        var blocked = benignCalls.Count(d => d is AgentSendStatus.Blocked or AgentSendStatus.Duplicate or AgentSendStatus.Refused);
        var held = benignCalls.Count(d => d == AgentSendStatus.Held);

        return new ConfigSummary(
            config,
            results.Count,
            attacks.Count,
            attacks.Count(a => a.Stopped),
            Math.Round((double)attacks.Count(a => a.Stopped) / attacks.Count, 4),
            benignCalls.Count,
            blocked,
            held,
            Math.Round((double)blocked / benignCalls.Count, 4),
            Math.Round((double)held / benignCalls.Count, 4),
            Math.Round(Percentile(0.5), 2),
            Math.Round(Percentile(0.99), 2),
            attacks.GroupBy(a => a.Scenario.Group)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key.ToString(), g => $"{g.Count(a => a.Stopped)}/{g.Count()}"),
            [.. attacks.Where(a => !a.Stopped).OrderBy(a => a.Scenario.Id, StringComparer.Ordinal)
                .Select(a => $"{a.Scenario.Id}: {a.Scenario.Description}{(a.Scenario.KnownLimitation ? " (known limitation)" : string.Empty)}")]);
    }
}
