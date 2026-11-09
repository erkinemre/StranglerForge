# Rollback strategy
Routing back is safe only while legacy data is current. After write ownership transfers, reconcile or replay before reverting writes. Define checkpoints, monitor errors, retain event history, and rehearse recovery.

## Inventory rollback gate
Inventory route and monolith mode rollback is safe only before PostgreSQL accepts new Inventory writes. After that, reconcile every reservation, adjustment, confirmation, and release into SQL Server before changing the authoritative writer. The development cutover has occurred and PostgreSQL has accepted new Inventory writes; a route-only rollback is unsafe. Reconcile all post-cutover changes into SQL Server before changing ownership.

## Orders rollback gate
The development Orders cutover imported and verified 64 historical SQL orders, then accepted new PostgreSQL orders. SQL remains at 64 historical rows. A route-only rollback would hide the new Orders and may duplicate payment or reservation work. Reconcile each new order, saga state, Inventory reservation, payment attempt, and notification before changing the authoritative writer. See [Order cutover](orders-cutover.md).

Phase 07 implements [rollback eligibility](safe-rollback.md) in the gateway. Current development Catalog, Inventory, and Orders ownership rollback is blocked because PostgreSQL has accepted new writes and SQL reconciliation is unverified. Deployment rollback that retains modern data ownership is a separate operation.
