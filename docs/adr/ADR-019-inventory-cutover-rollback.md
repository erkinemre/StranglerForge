# ADR-019: Inventory cutover and rollback

Status: Accepted; development cutover completed after reconciliation.

Freeze order, payment, and stock writes; translate legacy available stock and pending orders to Inventory total/reserved quantities; verify GUIDs, counts, totals, and source stability; then switch YARP and monolith mode together. Do not re-import after Inventory accepts writes. Route-only rollback is allowed only before new Inventory writes. Later rollback requires replay/reconciliation into SQL Server. A blind one-time copy and immediate switch were rejected because source writes and pending reservations can be lost. The development source was reconciled and Inventory ownership switched to PostgreSQL in Phase 05.
