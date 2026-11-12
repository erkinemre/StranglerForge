# Bounded contexts
Customer owns profiles; Catalog owns products and prices; Inventory owns stock and reservations; Orders owns lifecycle and line snapshots; Payments owns simulated attempts; Notifications owns delivery requests. Candidate aggregate roots are Customer, Product, StockItem, Order, PaymentAttempt, and NotificationRequest. Order lines belong to Order.
Phase 03 adds a routing-only gateway outside all bounded contexts. Every context still runs in the monolith and shares SQL Server.

## Phase 05 Inventory boundary
Inventory is an independently deployable bounded context for stock, reservations, adjustments, and inventory audit. Catalog alone owns product descriptions and prices. Orders retain customer intent, price snapshots, payment status, and customer notifications in the monolith. Inventory stores only stable product/order identifiers. Its HTTP boundary supplies immediate reservation decisions; versioned integration events supply asynchronous audit facts.

## Final hybrid boundary

Catalog, Inventory, and Orders are independent deployments with private PostgreSQL databases. Customer, simulated Payment, and Notification remain separate domain modules within the SQL Server monolith. The gateway owns no commerce aggregate. No service queries another service database; cross-context requests use HTTP or versioned events. See [final review](final-engineering-review.md).
