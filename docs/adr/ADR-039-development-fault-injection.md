# ADR-039: Development-only fault injection

Status: Accepted for Phase 08.

## Context

Resilience tests need repeatable timeouts, 503 responses, slow dependencies, and worker/consumer interruption without damaging shared databases or exposing public chaos controls.

## Decision

Enable HTTP fault middleware only when the host is in Development and `FaultInjection:Enabled` is true. Request-triggered faults require loopback plus a secret token; health endpoints are exempt. Saga pause and audit consumer failure are Development-only configuration switches. Database and broker outages are exercised with isolated test endpoints and credentials rather than destructive server actions.

## Consequences

Fault injection remains off by default. Tests must explicitly set development configuration and verify the fault mode; live broker recovery is a separate infrastructure gate.
