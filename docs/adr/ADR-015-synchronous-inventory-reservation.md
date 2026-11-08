# ADR-015: Synchronous reservation, asynchronous facts

Status: Accepted.

Order creation calls Inventory over bounded HTTP with an order-derived stable reservation ID and status lookup after ambiguous timeouts. Payment confirms or releases the reservation synchronously. Inventory emits facts asynchronously through RabbitMQ. The order needs a definitive stock decision before returning HTTP 201; fire-and-forget reservation messages would produce uncertain orders. A distributed transaction was rejected because it couples SQL Server and PostgreSQL and is fragile across service outages. This choice keeps order latency coupled to Inventory availability and requires explicit compensation/reconciliation.

For client retries of `POST /api/orders`, an optional GUID `Idempotency-Key` becomes the order and reservation ID. Existing clients remain compatible; clients without the key cannot assume order creation is idempotent.
