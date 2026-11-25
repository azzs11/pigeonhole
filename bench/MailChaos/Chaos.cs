using System.Collections.Concurrent;
using Relay.Application;

namespace MailChaos;

internal enum ProviderFault
{
    None,
    CrashBeforeSend,
    CrashAfterAccept,
    TimeoutBeforeAccept,
    TimeoutAfterAccept,
    Outage,
}

/// <summary>Thrown to simulate a worker (or API process) dying: nothing after it runs, no cleanup.</summary>
internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException()
        : base("Simulated process kill")
    {
    }

    public SimulatedCrashException(string message)
        : base(message)
    {
    }

    public SimulatedCrashException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Seeded fault decisions. With concurrency the draw order varies, so runs are statistically, not bit-for-bit, reproducible.</summary>
internal sealed class FaultPlan(int seed, double rate)
{
    private static readonly ProviderFault[] Kinds =
    [
        ProviderFault.CrashBeforeSend, ProviderFault.CrashAfterAccept, ProviderFault.TimeoutBeforeAccept,
        ProviderFault.TimeoutAfterAccept, ProviderFault.Outage,
    ];

    private readonly Random _random = new(seed);
    private readonly object _gate = new();

    public double Rate => rate;
    public ConcurrentDictionary<string, int> Injected { get; } = new(StringComparer.Ordinal);

    public ProviderFault NextProviderFault()
    {
        lock (_gate)
        {
            if (_random.NextDouble() >= rate)
            {
                return ProviderFault.None;
            }

            var fault = Kinds[_random.Next(Kinds.Length)];
            Count(fault.ToString());
            return fault;
        }
    }

    public bool Chance(double p)
    {
        lock (_gate)
        {
            return _random.NextDouble() < p;
        }
    }

    public int Between(int min, int max)
    {
        lock (_gate)
        {
            return _random.Next(min, max);
        }
    }

    public void Count(string fault) => Injected.AddOrUpdate(fault, 1, (_, n) => n + 1);
}

/// <summary>Ground truth: every email the provider actually accepted, keyed by subject (one per logical message).</summary>
internal sealed class Ledger
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _deliveries = new(StringComparer.Ordinal);
    private long _sequence;

    public string Record(string subject, DateTimeOffset at)
    {
        _deliveries.GetOrAdd(subject, _ => new ConcurrentQueue<DateTimeOffset>()).Enqueue(at);
        return $"chaos-{Interlocked.Increment(ref _sequence)}";
    }

    public int Distinct => _deliveries.Count;
    public int Total => _deliveries.Values.Sum(q => q.Count);

    public IEnumerable<(string Subject, DateTimeOffset First)> Firsts() =>
        _deliveries.Select(kv => (kv.Key, kv.Value.Min()));
}

/// <summary>An in-memory provider that injects the planned faults around a real "accept" into the ledger.</summary>
internal sealed class ChaosProvider(FaultPlan plan, Ledger ledger, Action<OutgoingEmail, string>? onAccepted) : IEmailProvider
{
    public string Name => "chaos";
    public bool SupportsIdempotentRetry => false;

    public Task<ProviderOutcome> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        switch (plan.NextProviderFault())
        {
            case ProviderFault.Outage:
                return Task.FromResult(ProviderOutcome.Transient("provider outage"));
            case ProviderFault.TimeoutBeforeAccept:
                return Task.FromResult(ProviderOutcome.Unknown("timeout before the provider accepted"));
            case ProviderFault.CrashBeforeSend:
                throw new SimulatedCrashException();
            case ProviderFault.TimeoutAfterAccept:
                Accept(email);
                return Task.FromResult(ProviderOutcome.Unknown("timeout after the provider accepted"));
            case ProviderFault.CrashAfterAccept:
                Accept(email);
                throw new SimulatedCrashException();
            default:
                return Task.FromResult(ProviderOutcome.Sent(Accept(email)));
        }
    }

    private string Accept(OutgoingEmail email)
    {
        var id = ledger.Record(email.Subject, DateTimeOffset.UtcNow);
        onAccepted?.Invoke(email, id);
        return id;
    }
}
