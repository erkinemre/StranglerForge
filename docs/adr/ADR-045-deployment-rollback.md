# ADR-045: Roll back deployments without reversing ownership

Status: Accepted for Phase 09.

A Kubernetes or Compose image rollback restores an application artifact only. It does not switch the authoritative database writer. Gateway configuration rollback requires a shared migration revision check; transfer to legacy ownership additionally requires reconciliation evidence, no unaccounted writes, and the established rollback process. Database migrations must be backward compatible for the rollback window; restore from verified backup when a schema change cannot be reversed safely.