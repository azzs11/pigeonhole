using System.Collections.Concurrent;
using Polly.CircuitBreaker;
using Relay.Application;

namespace Relay.Infrastructure.Delivery;

public sealed record ProviderSnapshot(string Name, int Position, string CircuitState, int Attempts, int Failures, string? LastError);

/// <summary>Sliding 5-minute attempt and failure counts per provider, plus the circuit breaker's live state.</summary>
internal sealed class ProviderStats(TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(DateTimeOffset At, bool Ok)>> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _lastError = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, CircuitBreakerStateProvider> Circuits { get; } = new(StringComparer.Ordinal);

    public void Record(string provider, ProviderOutcomeKind kind, string? detail)
    {
        var ok = kind is ProviderOutcomeKind.Sent or ProviderOutcomeKind.Rejected;
        _attempts.GetOrAdd(provider, _ => new()).Enqueue((time.GetUtcNow(), ok));
        if (!ok && detail is not null)
        {
            _lastError[provider] = detail.Length > 300 ? detail[..300] : detail;
        }
    }

    public IReadOnlyList<ProviderSnapshot> Snapshot(IReadOnlyList<string> order)
    {
        var since = time.GetUtcNow() - Window;
        return [.. order.Select((name, i) =>
        {
            var queue = _attempts.GetOrAdd(name, _ => new());
            while (queue.TryPeek(out var head) && head.At < since)
            {
                queue.TryDequeue(out _);
            }

            var items = queue.ToArray();
            var state = Circuits.TryGetValue(name, out var c) ? c.CircuitState.ToString() : "Closed";
            return new ProviderSnapshot(name, i, state, items.Length, items.Count(a => !a.Ok), _lastError.GetValueOrDefault(name));
        })];
    }
}

/// <summary>Optional hook around every provider call. The sandbox uses it to inject faults.</summary>
public interface IProviderInterceptor
{
    Task<ProviderOutcome> SendAsync(IEmailProvider provider, OutgoingEmail email, Func<CancellationToken, Task<ProviderOutcome>> send, CancellationToken cancellationToken);
}
