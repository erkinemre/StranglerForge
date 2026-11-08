# ADR-022: No Distributed Database Transaction

Status: Accepted.

## Context
Order, Inventory, and Payment data are in separate databases and cannot share an atomic commit.

## Decision
Use local transactions plus a persisted saga and explicit compensation. Orders state and outbound transition events commit together through the EF bus outbox. Inventory reserve, confirm, and release use stable IDs. The monolith payment operation is keyed by OrderId.

## Consequences
Temporary disagreement is possible. Reconciliation uses saga state and idempotent remote lookups. A route-only rollback after new Orders writes is unsafe.
