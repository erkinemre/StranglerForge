# Resilience and retry safety

`AddSafeReadResilience` applies the supported .NET HTTP resilience pipeline to extracted HTTP dependencies. Orders uses distinct named clients for Legacy, Catalog, and Inventory, so one dependency's circuit does not open another dependency's circuit. Legacy and Inventory Catalog clients use the same bounded policy. The gateway's YARP routes retain their existing timeout policy; it does not stack an additional retry pipeline.

| Operation | Automatic retry | Timeout and ambiguous outcome |
| --- | --- | --- |
| GET customer, product, stock, reservation | Up to two retries on transient infrastructure failure, exponential jitter | 2-second attempt, 6-second total; 7-second `HttpClient` ceiling |
| POST reservation, payment, stock, notification, order | None | A timeout may follow a commit; retain the stable operation ID and reconcile through the existing workflow |
| MassTransit delivery | Existing broker/consumer retry and outbox | Durable outbox/inbox semantics remain the recovery source; no HTTP fallback to SQL |

The HTTP circuit samples 30 seconds, requires at least 10 calls, opens for 10 seconds, and counts transient server/timeout failures. Ordinary 400/404 business responses do not trip it. Polly emits `resilience.polly.strategy.events` for retry, timeout, and closed/open/half-open transitions. The gateway admits at most 64 simultaneous non-health, non-admin requests and rejects excess requests with 429 without an in-memory queue. Health and migration administration bypass this load limiter.

An uncertain payment is returned as `PaymentOutcome.Uncertain`; the persisted saga keeps it pending for operator review. A reservation timeout is reconciled by its durable reservation/order identifier. The development fault hooks are disabled by default, require `Development` plus `FaultInjection:Enabled`, and accept request-triggered faults only with a loopback source and a configured token. Database/broker outage checks use isolated test connection settings; no fault hook mutates a shared database.
