# ADR-011: Extract Catalog first

Status: Accepted and implemented in Phase 04.

## Context
Catalog is read by orders and inventory but owns a small product model with stable GUIDs, making it the first practical extraction boundary.

## Decision
Move public Catalog routes and write authority to a PostgreSQL-backed Catalog Service while keeping non-catalog modules in the monolith.

## Alternatives considered
Keep Catalog in the monolith; extract Orders first; dual-write SQL Server and PostgreSQL.

## Consequences
New orders and stock additions depend on Catalog availability. Historical order snapshots remain valid.

## Trade-offs
Operational complexity grows, but a single writer and explicit dependency make the migration measurable and reversible before new writes.
