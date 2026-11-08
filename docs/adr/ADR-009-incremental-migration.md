# ADR-009: Incremental Migration

Status: Accepted for target architecture; implementation deferred.

## Context
Wholesale rewrite risks schedule and regression.

## Decision
Extract and verify one context at a time.

## Alternatives considered
Big bang rewrite.

## Consequences
Coexistence temporarily increases complexity.

## Trade-offs
The decision favors controlled migration and explicit ownership at the cost described above.
