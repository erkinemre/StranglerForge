# ADR-010: PostgreSQL Extracted Services

Status: Accepted for target architecture; implementation deferred.

## Context
New services need independent persistence.

## Decision
Use PostgreSQL per extracted service.

## Alternatives considered
SQL Server per service; shared SQL Server.

## Consequences
Two database platforms increase operating demands.

## Trade-offs
The decision favors controlled migration and explicit ownership at the cost described above.
