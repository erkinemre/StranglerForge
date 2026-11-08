# Integration strategy
Use HTTP for immediate reads and commands; versioned integration events for asynchronous state propagation. Domain events remain private. Later outbox/inbox processing supports recovery and idempotency. Propagate correlation IDs without logging secrets. Distinguish validation errors, domain conflicts, and infrastructure faults.
