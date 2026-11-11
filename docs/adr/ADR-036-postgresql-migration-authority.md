# ADR-036: PostgreSQL migration authority for gateway replicas

Status: Accepted for Phase 08 replica deployments.

## Context

The Phase 07 JSON file serializes transitions only within one gateway process. Replicas could accept divergent transitions or route writes using stale configuration.

## Decision

Use a dedicated PostgreSQL migration database when replicas are enabled. Each API request verifies the active flag fingerprint under a shared row lock and holds that lock through forwarding. Transitions acquire an exclusive row lock, check expected versions, and commit the snapshot and audit atomically. Configuration activation uses a global revision and separate audit table; stale instances fail closed. `/health/ready` checks the authority. The file mode remains for one-instance local development.

## Consequences

A temporarily unavailable authority causes 503 for API traffic; this is intentional to prevent unsafe ownership writes. Long-running requests delay transitions and use pooled database connections. Cutover still needs an external freeze, verified checkpoint, and drain procedure. The SQL schema is initialized at first access; production deployments should provision and review database permissions before traffic.
