using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Relay.Infrastructure.Observability;

/// <summary>
/// Masks email addresses in every string log property: "alice@example.com" becomes
/// "a***@example.com". Keeps the domain, which is what you need when debugging delivery.
/// </summary>
public sealed partial class EmailMaskingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            var masked = Mask(value);
            if (!ReferenceEquals(masked, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, masked));
            }
        }
    }

    public static string Mask(string text) =>
        Address().Replace(text ?? string.Empty, m => $"{m.Groups["first"].Value}***@{m.Groups["domain"].Value}");

    private static LogEventPropertyValue Mask(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string s } when Address().IsMatch(s) => new ScalarValue(Mask(s)),
        SequenceValue seq => new SequenceValue(seq.Elements.Select(Mask)),
        StructureValue st => new StructureValue(st.Properties.Select(p => new LogEventProperty(p.Name, Mask(p.Value))), st.TypeTag),
        _ => value,
    };

    [GeneratedRegex(@"(?<first>[A-Za-z0-9])[A-Za-z0-9._%+\-]*@(?<domain>[A-Za-z0-9.\-]+\.[A-Za-z]{2,})", RegexOptions.CultureInvariant)]
    private static partial Regex Address();
}

public sealed class QueueMetrics
{
    private readonly Dictionary<string, long> _byStatus = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _outboxDepth;

    public QueueMetrics()
    {
        Application.RelayTelemetry.Meter.CreateObservableGauge("relay.messages.by_status", ObserveStatuses, "{message}",
            "Messages currently in each delivery state (sampled).");
        Application.RelayTelemetry.Meter.CreateObservableGauge("relay.outbox.depth", () => Interlocked.Read(ref _outboxDepth), "{row}",
            "Outbox rows not yet processed (sampled).");
    }

    public void Update(IReadOnlyDictionary<string, long> byStatus, long outboxDepth)
    {
        ArgumentNullException.ThrowIfNull(byStatus);
        lock (_gate)
        {
            _byStatus.Clear();
            foreach (var (status, count) in byStatus)
            {
                _byStatus[status] = count;
            }
        }

        Interlocked.Exchange(ref _outboxDepth, outboxDepth);
    }

    private IEnumerable<System.Diagnostics.Metrics.Measurement<long>> ObserveStatuses()
    {
        lock (_gate)
        {
            return [.. _byStatus.Select(kv => new System.Diagnostics.Metrics.Measurement<long>(
                kv.Value, new KeyValuePair<string, object?>("status", kv.Key)))];
        }
    }
}
