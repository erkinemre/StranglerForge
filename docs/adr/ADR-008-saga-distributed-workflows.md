# ADR-008: Saga Distributed Workflows

Status: Accepted; Phase 06 implements a persisted Orders saga.

## Context
Orders span independently owned data.

## Decision
Coordinate later order workflows with saga state and compensation.

## Alternatives considered
Distributed transaction; synchronous chain.

## Consequences
Compensation is business-specific.

## Trade-offs
The decision favors controlled migration and explicit ownership at the cost described above.
