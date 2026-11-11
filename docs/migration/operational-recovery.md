# Operational recovery

- **Modern service unavailable:** Keep its write route on the authoritative owner. Return an error; restore service or database readiness. Do not switch to stale SQL.
- **Legacy service unavailable:** Customer, payment, and notification requests remain unavailable. Do not fabricate a modern fallback. Persisted Order sagas retain retry state; uncertain payment outcomes require manual review.
- **Gateway timeout or ambiguous write response:** Use the same idempotency or operation ID to inspect the authoritative store before retrying. Never send the operation to another writer.
- **Invalid flags or partial deployment:** Restore the last validated configuration and verify all YARP clusters and ownership modes together. Conflicting requests fail closed.
- **Shadow timeout or contract mismatch:** The primary response remains authoritative. Inspect bounded comparison diagnostics and the correlation ID; stop canary advancement until the mismatch is understood.
- **Rollback during active saga:** Keep modern ownership, enter `RollbackPending` or `ReconciliationRequired`, and allow existing idempotent saga recovery commands. Verify reservations, payments, notifications, and outbox state before any data transfer.
- **Database connectivity failure:** Repair the authoritative database; do not rerun one-shot importers after post-cutover writes.

The administrative API is local-only and disabled without `MigrationControl__AdminToken`. Its file mode is single-instance. Multi-replica deployments use the dedicated PostgreSQL authority and still require independently verified data checkpoints. RabbitMQ and Docker Compose runtime checks remain outstanding; this document does not claim broker recovery was tested.
