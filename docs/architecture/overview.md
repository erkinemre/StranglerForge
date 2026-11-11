# Architecture overview

LegacyCommerce began as one deployment with Customer, Catalog, Inventory, Orders, Payments, and Notifications modules sharing SQL Server. Phase 03 put YARP in front of the monolith. Subsequent development cutovers moved Catalog, Inventory, and Orders behind YARP to separate ASP.NET Core hosts and PostgreSQL databases. Customers, Payments, and Notifications remain in LegacyCommerce. See the [current architecture](current-architecture.md) and [migration roadmap](../migration/migration-roadmap.md).

Each extracted service owns its data and never reads another service database. Synchronous HTTP boundaries support product and customer lookup, stock reservation, payment intent, and notification commands. Versioned integration events use MassTransit, transactional outboxes, and idempotent consumers. The Orders workflow uses a persisted application saga with stable command IDs; it is not a MassTransit state machine. Separate database transactions create eventual consistency and require compensation or operator review when remote outcomes remain uncertain.

The target architecture may extract more modules in later phases, but those steps are not implemented. Local Docker remains unavailable. CI runs disposable PostgreSQL/RabbitMQ Testcontainers, while Compose interpolation and image builds pass; full application Compose startup is not established. [Rollback guidance](../migration/rollback-strategy.md) requires reconciling data ownership before route reversal.

Phase 07 adds [migration control](migration-control.md) in the gateway: validated flags, a versioned local lifecycle, Catalog-only pre-cutover read canary and shadow comparison, and fail-closed writer checks. It does not transfer data ownership or create a legacy fallback for the extracted services.

Phase 10 records the final hybrid architecture, explicit remaining legacy boundaries, security limitations, and validation evidence in the [final engineering review](final-engineering-review.md).
