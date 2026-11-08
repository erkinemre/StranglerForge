# ADR-004: YARP Reverse Proxy

Status: Accepted and implemented in Phase 03.

## Context
Clients need a stable address during migration.

## Decision
Introduce routing-only YARP in Phase 03.

## Alternatives considered
Custom proxy; client-side routing.

## Consequences
Adds an operational component.

## Trade-offs
The decision favors controlled migration and explicit ownership at the cost described above.
