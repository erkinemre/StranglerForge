# ADR-032: Rollback requires verified reconciliation

Status: Accepted.

A route or feature flag change cannot make historical SQL tables authoritative after PostgreSQL has accepted writes. Rollback eligibility reports modern ownership, post-checkpoint writes, missing checkpoints, and unverified saga, reservation, payment, and outbox clearance. `RollbackPending` and `ReconciliationRequired` retain the modern writer. Operators must freeze writes, reconcile and verify data, and review transaction recovery before a separate ownership transfer. This phase deliberately provides no destructive or automatic reconciliation tool.
