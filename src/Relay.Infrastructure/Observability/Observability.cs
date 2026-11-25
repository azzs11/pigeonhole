using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Relay.Application;
using Serilog;
using Serilog.Formatting.Compact;

namespace Relay.Infrastructure.Observability;

public sealed record TelemetryOptions
{
    public const string Section = "Relay:Telemetry";

    /// <summary>OTLP/HTTP traces endpoint, e.g. http://localhost:4318/v1/traces (Jaeger). Empty: no trace export.</summary>
    public Uri? OtlpTracesEndpoint { get; init; }

    /// <summary>OTLP/HTTP metrics endpoint, e.g. http://localhost:9090/api/v1/otlp/v1/metrics (Prometheus). Empty: no metric export.</summary>
    public Uri? OtlpMetricsEndpoint { get; init; }
}

public static class ObservabilityExtensions
{
    /// <summary>
    /// JSON logs (Serilog, addresses masked) plus OpenTelemetry traces and metrics for the
    /// "Relay" source, Npgsql and outgoing HTTP. Exporters are on only when endpoints are set.
    /// </summary>
    public static OpenTelemetryBuilder AddRelayObservability(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSerilog((services, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .Enrich.With<EmailMaskingEnricher>()
            .WriteTo.Console(new CompactJsonFormatter()));

        var options = builder.Configuration.GetSection(TelemetryOptions.Section).Get<TelemetryOptions>() ?? new();
        return builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t =>
            {
                t.AddSource(RelayTelemetry.Name, "Npgsql").AddHttpClientInstrumentation();
                if (options.OtlpTracesEndpoint is not null)
                {
                    t.AddOtlpExporter(o => Http(o, options.OtlpTracesEndpoint));
                }
            })
            .WithMetrics(m =>
            {
                m.AddMeter(RelayTelemetry.Name, "Npgsql").AddHttpClientInstrumentation().AddRuntimeInstrumentationIfAvailable();
                if (options.OtlpMetricsEndpoint is not null)
                {
                    m.AddOtlpExporter((o, reader) =>
                    {
                        Http(o, options.OtlpMetricsEndpoint);
                        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 10_000;
                    });
                }
            });
    }

    private static MeterProviderBuilder AddRuntimeInstrumentationIfAvailable(this MeterProviderBuilder m) =>
        m.AddMeter("System.Runtime");

    private static void Http(OtlpExporterOptions o, Uri endpoint)
    {
        o.Endpoint = endpoint;
        o.Protocol = OtlpExportProtocol.HttpProtobuf;
    }
}
