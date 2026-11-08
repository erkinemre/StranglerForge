# ADR-021: Persisted Order Saga

Status: Accepted for Phase 06.

## Context
Order submission spans independently owned Orders, Inventory, and monolith Payment/Notification data. A process restart or lost HTTP response must not erase progress.

## Decision
Persist `OrderSaga` state in Orders PostgreSQL and advance it with a background worker using conditional state updates. Use stable OrderId for all remote commands. Publish state transitions with MassTransit EF bus outbox. The preferred MassTransit state-machine saga was not selected because the Phase 06 command path already depends on synchronous HTTP boundaries and a durable explicit state machine is smaller to teach and test here.

## Consequences
Inventory and payment calls can repeat after ambiguous responses, so each boundary must be idempotent. Payment uncertainty holds stock for review. Live multi-replica and broker recovery behavior remains to be validated.
