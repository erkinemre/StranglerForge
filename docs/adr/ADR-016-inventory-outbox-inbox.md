# ADR-016: MassTransit EF transactional outbox and inbox

Status: Accepted.

Inventory writes stock transitions and MassTransit EF outbox rows in one PostgreSQL transaction. Its delivery service publishes after commit. The audit consumer uses MassTransit EF consumer outbox/inbox and an EventId unique key. This prevents a broker outage from rolling back committed stock and survives service restart. Publishing immediately after SaveChanges was rejected because a crash can lose the event. A custom polling outbox was rejected because MassTransit supplies the needed durable delivery and inbox. At-least-once delivery still requires idempotent consumers and queue monitoring.
