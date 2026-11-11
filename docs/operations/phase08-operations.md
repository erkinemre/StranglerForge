# Phase 08 operations and incident diagnostics

## Local and Compose setup

For a local .NET host, set `OTEL_EXPORTER_OTLP_ENDPOINT` to a reachable collector URI such as `http://127.0.0.1:4317`. Leaving it unset disables OTLP export while retaining local instrumentation. In Compose, the endpoint is injected as `http://otel-collector:4317`. Set the existing database/broker secrets and also `MIGRATION_POSTGRES_PASSWORD` and `GRAFANA_ADMIN_PASSWORD`; `OTEL_TRACES_SAMPLING_PERCENTAGE` defaults to 100 for this development stack. Run `docker compose -f deploy/docker/compose.yml config` before startup, then follow the existing database migration and write-freeze procedure before starting post-cutover application traffic. No Compose runtime verification was possible in the authoring environment because Docker CLI/Engine were absent.

Jaeger UI binds to `127.0.0.1:16686`, Prometheus to `127.0.0.1:9090`, Grafana to `127.0.0.1:3000`, and the Collector's local OTLP ingress to `127.0.0.1:4317`. RabbitMQ metrics remain on the internal Compose network. Grafana provisions Prometheus and Jaeger data sources and seven dashboards in the StranglerForge folder. Dashboard JSON files were parsed locally; dashboard queries and runtime data require the Compose stack.

## Metrics catalog and dashboards

| Dashboard | Instrumented sources | Investigation |
| --- | --- | --- |
| System Overview | `http.server.request.duration` | Request rate, 5xx rate, P95 by service |
| Gateway Traffic and Migration | `migration.events` with bounded `kind` | Owner conflicts, canary distribution, shadow mismatches |
| Service Latency and Errors | `http.server.request.duration`, `http.client.request.duration` | P50/P95/P99 and downstream latency |
| Order Processing and Saga | `stranglerforge.orders.submitted`, `.transitions`, `.saga_step.duration`, `.compensation.attempts`, `.sagas.active`, `.payments.pending` | Submitted/terminal transitions, slow saga steps, compensation, pending work |
| Inventory Operations | `stranglerforge.inventory.reservations`, `.reservation.duration`, `.duplicates`, `.concurrency_conflicts` | Reservation outcomes, latency, duplicate detection, conflicts |
| RabbitMQ and Outbox | `stranglerforge.outbox.pending`, `.oldest.age`; RabbitMQ `rabbitmq_detailed_queue_messages_ready` and `.consumers`; `stranglerforge.consumer.failures` | Distinguish database backlog from broker queue backlog and error queues |
| Resilience and Circuit Breakers | `stranglerforge.dependency.failures`, Polly `resilience.polly.strategy.events` | Open/half-open/closed transitions, retries, timeouts |

Prometheus converts dots to underscores and appends `_total` to counters and `_seconds` to second-based units. The dashboard expressions use those exported names. Service, state, outcome, kind, queue, and pipeline are bounded dimensions; no order/customer/reservation/correlation identifiers are labels. Outbox gauges omit samples older than 30 seconds, so absence of a series can mean a probe or database failure; check service readiness and logs before interpreting zero backlog. Broker queue metrics are scraped from RabbitMQ's native Prometheus plugin, not inferred from application state.

## Health and incident sequence

`/health/live` proves the process can answer HTTP without checking downstream systems. `/health/ready` checks the host's critical database, and the gateway checks its shared migration authority when configured. `/health` retains historical behavior for compatibility. Health responses do not include connection strings. A downstream outage may make business operations fail without making the process dead.

1. Start with System Overview to locate the first service with elevated 5xx or P95. Correlate a request using `TraceId` in JSON logs and Jaeger; use `X-Correlation-ID` for application records.
2. Check Gateway Traffic and Migration for `ownership.conflict`, invalid configuration, or authority unavailable counters. Verify every gateway replica's `/health/ready` before changing flags.
3. Check Resilience and Circuit Breakers for `OnCircuitOpened` and `OnTimeout`; inspect the named dependency in logs. A business 400 or insufficient-stock response is not an infrastructure failure.
4. If orders remain pending, inspect Order Processing and Saga, outbox age, and RabbitMQ queue depth. Do not mark a payment declined after a timeout; use the existing manual reconciliation path.
5. For migration rollback, follow [safe rollback](../migration/safe-rollback.md). The gateway rejects transfer of modern-owned data back to SQL without verified reconciliation.

## Development fault injection

Set `ASPNETCORE_ENVIRONMENT=Development`, `FaultInjection__Enabled=true`, and a local secret `FaultInjection__Token`. A loopback caller may send `X-Dev-Fault-Token` and `X-Dev-Fault: 503|slow|timeout` to an application route. Health routes are exempt. `slow` adds 500 ms; `timeout` waits up to five seconds and returns 503 unless the request is canceled. `FaultInjection__PauseSagaWorker=true` pauses polling; `FaultInjection__FailOrderAuditConsumer=true` makes the development audit consumer throw so MassTransit retries/error handling can be inspected. Database and broker outages are simulated with unreachable endpoints only in isolated test deployments. All hooks are disabled outside Development, and no public endpoint can activate them.

The live PostgreSQL coordination test is separate from the normal solution suite. Set `MIGRATION_TEST_CONNECTION` to a test database whose user may create schemas, then run `dotnet test tests/LiveInfrastructureTests/MigrationAuthority.LiveTests`. The test creates and removes only its own random schema. Live Collector/Jaeger/Prometheus/Grafana, RabbitMQ propagation, Compose startup, and multi-replica gateway behavior remain runtime gates wherever Docker is unavailable.
