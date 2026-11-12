# Data ownership
Initially contexts share SQL Server. Extract one context at a time to service-owned PostgreSQL. Backfill and reconcile before switching the authoritative writer. Other contexts use APIs or events, never direct database access. Avoid dual writes.
Phase 04 transfers Product Catalog writes to PostgreSQL after a write freeze, import, and verification. The legacy SQL Product table remains for audit and rollback but is not used by extracted-mode workflows.

## Phase 05 ownership gate
Before Inventory cutover, SQL Server Stock remains authoritative. After a verified write freeze and import, Inventory PostgreSQL becomes the only authoritative Inventory writer; SQL Stock is historical. The one-shot migration tool alone reads both stores. Development Inventory data passed this gate in Phase 05. See docs/migration/inventory-cutover.md.

## Phase 06 ownership gate
After the verified 64-row development import, Orders PostgreSQL owns order creation, state, saga progress, and transition events. SQL order rows are historical. The monolith still owns payment attempts and notifications through token-protected HTTP boundaries. See [Order cutover](../migration/orders-cutover.md).

## Phase 07 traffic controls

A gateway flag or migration state does not transfer database ownership. All three extracted PostgreSQL stores have post-cutover writes; SQL Catalog, Inventory, and Orders data is historical. The gateway rejects conflicting writer clusters and reports legacy ownership rollback as ineligible until independently verified reconciliation. See [safe rollback](../migration/safe-rollback.md).

## Phase 10 conclusion

No ownership transfer occurs in Phase 10. Catalog, Inventory, and Orders PostgreSQL remain authoritative. SQL Server retains historical rows plus active Customer, Payment, and Notification data. Legacy extracted execution paths are retained behind ownership gates until dependency inventory and the rollback window are resolved; no business records are deleted. Production Inventory write commands now require distinct administrative and reservation tokens.
