# ADR-007: Transactional Outbox and Inbox

Status: Accepted for target architecture; implementation deferred.

## Context
Database commits and broker publication are not atomic.

## Decision
Introduce outbox and inbox with messaging.

## Alternatives considered
Best-effort publish; distributed transactions.

## Consequences
Adds storage and processing complexity.

## Trade-offs
The decision favors controlled migration and explicit ownership at the cost described above.
