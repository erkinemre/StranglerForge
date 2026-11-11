# ADR-033: OpenTelemetry across HTTP and messaging boundaries

Status: Accepted for Phase 08.

## Context

The extracted services already pass correlation IDs, but had no common trace or metric export path. Separate logging and tracing middleware would duplicate instrumentation and make cross-service latency difficult to diagnose.

## Decision

Use one shared .NET 10 OpenTelemetry registration in the gateway, monolith, Catalog, Inventory, and Orders hosts. Register W3C HTTP server/client traces, the MassTransit and Npgsql activity sources, built-in .NET HTTP meters, and bounded application meters. Export over OTLP only when an endpoint is configured. Resource attributes identify service, version, environment, and instance. Preserve `X-Correlation-ID` for application records and exclude business payloads and secrets from attributes.

## Consequences

The observability backend remains optional for local builds; trace continuity through a real broker must be checked in a live environment. Automatic and custom spans require cardinality review when new fields are added.
