# 0006. Observability: OTLP push, Prometheus, Jaeger, Grafana

- Status: accepted
- Date: 2026-11-25

## Decision

- The API and the worker export **OpenTelemetry** traces and metrics over OTLP/HTTP when
  `Relay:Telemetry` endpoints are set. Metrics go to Prometheus 3's native OTLP receiver
  (`/api/v1/otlp/v1/metrics`, every 10 s) and traces go to Jaeger v2. Both services push, so the
  worker needs no HTTP server for scraping.
- One `ActivitySource` and one `Meter`, both named `Relay`. Counters: accepted messages,
  state changes by status (counted after `SaveChanges`, so only durable changes count),
  provider sends by provider and outcome, webhook events by result. Histograms: provider
  send duration and outbox claim duration. Gauges sampled every 15 s by the worker:
  messages by status and outbox depth.
- **One trace per message.** The API request's W3C `traceparent` is stored on the outbox row.
  The worker's `relay.deliver` span uses it as its parent, so the trace runs from
  `POST /v1/messages` through the database to `relay.provider.send`.
- **Logs** are Serilog compact JSON to stdout. An enricher masks every email address in log
  properties (`a***@example.com`).
- `deploy/docker-compose.yml --profile observability` runs Prometheus (:9090), Jaeger (:16686)
  and Grafana (:3001), with a provisioned "Relay overview" dashboard.

## Consequences

- No exporter is configured by default, so tests and production opt in explicitly.
- Metric names follow Prometheus' OTLP translation: `relay_provider_sends_total`,
  `relay_outbox_claim_duration_milliseconds_bucket`, and so on.
