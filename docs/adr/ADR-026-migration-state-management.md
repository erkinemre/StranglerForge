# ADR-026: Gateway-local migration lifecycle

Status: Accepted for the single-instance reference deployment.

Phase 07 records `LegacyActive`, `ShadowValidation`, `CanaryReady`, `ModernActive`, `RollbackPending`, `ReconciliationRequired`, and `LegacyRetired` as explicit lifecycle states. Administrative transitions require an expected version and write state plus audit atomically to a gateway-local JSON file. The control API cannot change data ownership. This makes transitions reviewable and rejects concurrent stale commands without adding a distributed configuration store. The file is not safe for multi-replica control; a later deployment needs a coordinated store and leader or compare-and-set semantics. `LegacyRetired` has no automatic transition while LegacyCommerce still owns payments and notifications.

Phase 08 extension: the single-instance file decision is retained for local development and superseded for replica deployments by [ADR-036](ADR-036-postgresql-migration-authority.md).
