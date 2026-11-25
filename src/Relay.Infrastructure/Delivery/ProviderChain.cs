using System.Collections.Concurrent;
using System.Diagnostics;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Relay.Application;

namespace Relay.Infrastructure.Delivery;

/// <summary>
/// Tries providers in failover order. Each provider has its own pipeline:
/// retry (transient results only, exponential with jitter) → circuit breaker → timeout.
/// Fails over only when the outcome is definitely "not sent".
/// </summary>
public sealed class ProviderChain
{
    private readonly IReadOnlyList<IEmailProvider> _providers;
    private readonly DeliveryOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, ResiliencePipeline<ProviderOutcome>> _pipelines = new(StringComparer.Ordinal);

    private readonly ProviderStats _stats;
    private readonly IProviderInterceptor? _interceptor;

    public ProviderChain(
        IEnumerable<IEmailProvider> providers, DeliveryOptions options, TimeProvider time, IProviderInterceptor? interceptor = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _providers = [.. providers];
        _options = options;
        _time = time;
        _stats = new ProviderStats(time);
        _interceptor = interceptor;
    }

    /// <summary>Attempts, failures and circuit state per provider over the last 5 minutes.</summary>
    public IReadOnlyList<ProviderSnapshot> Snapshot() => _stats.Snapshot(Names);

    public IReadOnlyList<string> Names => [.. _providers.Select(p => p.Name)];

    public bool IsIdempotent(string? name) =>
        _providers.Any(p => p.SupportsIdempotentRetry && string.Equals(p.Name, name, StringComparison.Ordinal));

    public async Task<(ProviderOutcome Outcome, string Provider)> SendAsync(
        OutgoingEmail email, string? pinnedProvider, CancellationToken cancellationToken)
    {
        var candidates = _providers.Where(p => pinnedProvider is null || string.Equals(p.Name, pinnedProvider, StringComparison.Ordinal)).ToList();
        if (candidates.Count == 0)
        {
            candidates = [.. _providers];
        }

        (ProviderOutcome, string) last = (ProviderOutcome.Transient("No delivery provider is configured"), "none");
        foreach (var provider in candidates)
        {
            using var activity = RelayTelemetry.Source.StartActivity("relay.provider.send");
            activity?.SetTag("relay.provider", provider.Name);
            var started = _time.GetTimestamp();
            try
            {
                var outcome = await Pipeline(provider).ExecuteAsync(
                    static (state, token) => new ValueTask<ProviderOutcome>(state.interceptor is null
                        ? state.provider.SendAsync(state.email, token)
                        : state.interceptor.SendAsync(state.provider, state.email, t => state.provider.SendAsync(state.email, t), token)),
                    (provider, email, interceptor: _interceptor),
                    cancellationToken);
                _stats.Record(provider.Name, outcome.Kind, outcome.Detail);
                Record(provider.Name, outcome.Kind, started, activity, stats: false);
                if (outcome.Kind != ProviderOutcomeKind.Transient)
                {
                    return (outcome, provider.Name);
                }

                last = (outcome, provider.Name);
            }
            catch (BrokenCircuitException)
            {
                Record(provider.Name, ProviderOutcomeKind.Transient, started, activity, "circuit_open");
                last = (ProviderOutcome.Transient("Circuit open"), provider.Name);
            }
            catch (TimeoutRejectedException)
            {
                Record(provider.Name, ProviderOutcomeKind.Unknown, started, activity, "timeout");
                // The provider may have the message: never fail over, or we could send twice.
                return (ProviderOutcome.Unknown($"Timed out after {_options.ProviderTimeoutSeconds}s"), provider.Name);
            }
        }

        return last;
    }

    private void Record(string provider, ProviderOutcomeKind kind, long started, Activity? activity, string? reason = null, bool stats = true)
    {
        if (stats)
        {
            _stats.Record(provider, kind, reason ?? kind.ToString());
        }

        var outcome = reason ?? kind.ToString();
        activity?.SetTag("relay.outcome", outcome);
        RelayTelemetry.ProviderSends.Add(1,
            new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("outcome", outcome));
        RelayTelemetry.ProviderSendDuration.Record(_time.GetElapsedTime(started).TotalMilliseconds,
            new KeyValuePair<string, object?>("provider", provider));
    }

    private ResiliencePipeline<ProviderOutcome> Pipeline(IEmailProvider provider) =>
        _pipelines.GetOrAdd(provider.Name, _ =>
        {
            var builder = new ResiliencePipelineBuilder<ProviderOutcome> { TimeProvider = _time };
            var circuit = _stats.Circuits.GetOrAdd(provider.Name, _ => new CircuitBreakerStateProvider());
            if (_options.ProviderRetries > 0)
            {
                builder.AddRetry(new RetryStrategyOptions<ProviderOutcome>
                {
                    ShouldHandle = args => ValueTask.FromResult(args.Outcome.Result?.Kind == ProviderOutcomeKind.Transient),
                    MaxRetryAttempts = _options.ProviderRetries,
                    Delay = TimeSpan.FromMilliseconds(_options.ProviderRetryDelayMilliseconds),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                });
            }

            return builder
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions<ProviderOutcome>
                {
                    ShouldHandle = args => ValueTask.FromResult(
                        args.Outcome.Result?.Kind == ProviderOutcomeKind.Transient
                        || args.Outcome.Exception is TimeoutRejectedException),
                    FailureRatio = 0.5,
                    MinimumThroughput = 4,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    BreakDuration = TimeSpan.FromSeconds(30),
                    StateProvider = circuit,
                })
                .AddTimeout(TimeSpan.FromSeconds(_options.ProviderTimeoutSeconds))
                .Build();
        });
}
