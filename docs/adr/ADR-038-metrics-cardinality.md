# ADR-038: Bounded metric dimensions

Status: Accepted for Phase 08.

## Context

Prometheus series multiply by every unique label combination. Order, customer, reservation, and correlation identifiers would create unbounded cardinality and may expose sensitive business data.

## Decision

Use bounded service, state, outcome, migration kind, and pipeline labels. Keep high-cardinality IDs in structured logs and traces only when needed for diagnostics. Outbox probes omit stale samples instead of publishing fabricated zeros. RabbitMQ queue metrics come from its native Prometheus plugin.

## Consequences

Dashboards cannot filter by an individual order or customer; operators follow TraceId and CorrelationId into logs and Jaeger. New metrics require label cardinality review.
