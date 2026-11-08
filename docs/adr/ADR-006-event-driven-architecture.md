# ADR-006: Event-Driven Architecture

Status: Accepted for target architecture; implementation deferred.

## Context
Cross-service state changes must survive temporary outages.

## Decision
Use versioned integration events where asynchronous processing fits.

## Alternatives considered
Synchronous calls everywhere; shared tables.

## Consequences
Consumers handle duplicates and lag.

## Trade-offs
The decision favors controlled migration and explicit ownership at the cost described above.
