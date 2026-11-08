# ADR-005: Database per Service

Status: Accepted for target architecture; implementation deferred.

## Context
Shared tables block independent deployment.

## Decision
Give each extracted service exclusive write ownership.

## Alternatives considered
Shared database; distributed transactions.

## Consequences
Cross-service views need APIs or events.

## Trade-offs
The decision favors controlled migration and explicit ownership at the cost described above.
