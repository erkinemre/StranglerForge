# ADR-025: Eventual Consistency for Orders

Status: Accepted.

## Context
Inventory reservations, payment attempts, notifications, and Order status cannot change atomically across services.

## Decision
Return the persisted `Submitted` order immediately and expose later status by GET. The saga progresses through Inventory and Payment commands. Versioned `OrderTransitionedV1` events use a transactional outbox; the audit consumer uses a MassTransit inbox and unique EventId.

## Consequences
Clients poll after 201 and payment 202. RabbitMQ delivery is at least once. Database readiness is separate from messaging readiness so committed orders and outbox rows survive broker downtime.
