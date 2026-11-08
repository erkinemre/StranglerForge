# ADR-003: Modular Monolith Initial State

Status: Accepted for target architecture; implementation deferred.

## Context
Extraction needs a credible legacy baseline.

## Decision
Build one deployment with module boundaries in Phase 02.

## Alternatives considered
Disconnected services; unstructured monolith.

## Consequences
Shared data remains coupled initially.

## Trade-offs
The decision favors controlled migration and explicit ownership at the cost described above.
