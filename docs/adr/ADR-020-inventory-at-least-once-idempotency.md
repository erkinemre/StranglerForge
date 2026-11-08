# ADR-020: At-least-once delivery and separate idempotency keys

Status: Accepted.

RabbitMQ/MassTransit delivery is treated as at least once. HTTP reservation commands are deduplicated by stable reservation and order IDs in PostgreSQL. Integration messages are deduplicated by MassTransit Inbox MessageId for its retention window and by a durable EventId unique key at the audit side effect. End-to-end exactly-once delivery was rejected as an unsupported guarantee across databases and a broker. An infinite inbox retention window was rejected because it grows without bound; durable business keys protect the effect after inbox cleanup.
