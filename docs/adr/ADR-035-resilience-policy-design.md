# ADR-035: Dependency-specific HTTP resilience

Status: Accepted for Phase 08.

## Context

A universal retry policy could repeat a committed reservation or payment. Sharing one circuit between Catalog, Inventory, and payment would let one outage suppress unrelated calls.

## Decision

Use the supported Microsoft HTTP resilience handler with separate named Orders clients per dependency. Retry safe GETs at most twice with exponential jitter. Disable automatic POST/PUT/PATCH/DELETE retries. Use 2-second attempt and 6-second total budgets, 10-call circuit minimum over 30 seconds, and 10-second break duration. Preserve idempotent operation identifiers and explicit reconciliation for ambiguous mutations.

## Consequences

Known business rejections do not count as transient infrastructure faults. An uncertain payment remains pending for review. The per-dependency policies consume bounded memory but can return 503 quickly while open.
