# ADR-037: No Redis in migration coordination

Status: Accepted for Phase 08.

## Context

Redis can cache flags or broadcast invalidations, but Pub/Sub alone is not durable and expiring distributed locks are hard to reason about during long HTTP forwarding.

## Decision

Keep authoritative state, revisions, audit, and request/transition serialization in PostgreSQL. Do not add Redis to the critical migration path. A future read cache may be introduced only if it proves version freshness and fails closed on uncertainty.

## Consequences

Every controlled API request uses PostgreSQL, increasing load and making availability of the migration database part of gateway readiness. The consistency model is simpler and restart recovery is deterministic.
