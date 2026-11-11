# Multi-instance migration coordination

```mermaid
sequenceDiagram
  participant A as Gateway A
  participant DB as Migration PostgreSQL
  participant B as Gateway B
  A->>DB: BEGIN; SELECT state FOR SHARE
  B->>DB: SELECT state FOR UPDATE (waits)
  A->>A: Verify config hash, owner, route; serve request
  A->>DB: Release request lock
  DB-->>B: Exclusive lock granted
  B->>DB: Validate expected version; update state and audit; COMMIT
```

When `ConnectionStrings__MigrationControl` is configured, a dedicated PostgreSQL database becomes the migration authority. Each API request acquires a shared row lock, verifies the active configuration fingerprint, and holds the lock through response forwarding. An administrative transition takes an exclusive row lock, checks the expected service version, and commits its snapshot and bounded transition audit atomically. This blocks state transitions while an in-flight write is routed. A gateway unable to reach PostgreSQL or verify the active configuration returns 503 for API traffic. `/health/live` remains independent; `/health/ready` checks the authority.

The authoritative row also stores the serialized active flags and a global configuration revision. New deployment flags must be activated through authenticated `POST /internal/migration/config/activate` with `{ "expectedRevision": N }` after all gateway instances have received the intended configuration. Activation is audited separately in `migration_configuration_audit`. Instances with old flags fail closed until updated. The local file mode remains for a single-instance developer session and is not suitable for replica deployment. Redis is not needed: PostgreSQL row locks, durable storage, and revision checks supply the required coordination without Pub/Sub loss or lock expiry semantics.

The API remains loopback-plus-token protected and must not be exposed publicly. Cutover still requires an external write freeze and independently verified data checkpoint. State coordination cannot itself reconcile databases or prove that all old service requests have drained outside the gateway. Operators must stage configuration changes, check readiness on every replica, verify routing and data, then activate the new revision. See [Phase 08 operations](../operations/phase08-operations.md).
