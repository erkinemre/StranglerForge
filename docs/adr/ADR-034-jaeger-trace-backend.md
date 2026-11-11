# ADR-034: Jaeger for trace storage

Status: Accepted for the Phase 08 development stack.

## Context

Prometheus stores metrics and cannot answer trace queries. A reproducible local trace backend is needed without changing service code.

## Decision

Send OTLP traces from the Collector to Jaeger all-in-one. Grafana provisions Jaeger as a trace data source and Prometheus as a metric data source. The Collector enforces a bounded memory limit, sampling, and batching.

## Consequences

Jaeger all-in-one uses ephemeral local trace storage and is suitable for this educational Compose deployment, not a production retention plan. A durable backend and retention/security policy must be selected before production deployment.
