# ADR-013: Single-writer Catalog cutover

Status: Accepted and implemented for the forward cutover in Phase 04.

## Context
A route switch cannot atomically transfer database ownership, and unrestricted dual writes can diverge.

## Decision
Freeze legacy writes, import and verify SQL Products, then enable Catalog writes and disable legacy Catalog endpoints. Preserve GUIDs and reject target-only IDs during import.

## Alternatives considered
Big-bang rewrite; bidirectional synchronization; blind route rollback.

## Consequences
A maintenance window is needed. Post-cutover rollback requires audited data reconciliation.

## Trade-offs
The explicit freeze reduces availability briefly but prevents split-brain writes without speculative messaging infrastructure.
