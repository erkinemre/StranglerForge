# Current architecture: Phase 06 development cutover

```mermaid
flowchart LR
  Client --> Gateway[YARP Gateway]
  Gateway -->|Products| Catalog[Catalog Service]
  Gateway -->|Stock| Inventory[Inventory Service]
  Gateway -->|Orders| Orders[Order Service]
  Gateway -->|Customers, payments, notifications| Legacy[LegacyCommerce monolith]
  Catalog --> CatalogPg[(Catalog PostgreSQL)]
  Inventory --> InventoryPg[(Inventory PostgreSQL)]
  Orders --> OrdersPg[(Orders PostgreSQL)]
  Legacy --> Sql[(SQL Server)]
  Orders -->|Customer, payment, notification HTTP| Legacy
  Orders -->|Product price HTTP| Catalog
  Orders -->|Reservation HTTP| Inventory
  OrdersPg -->|Outbox| Rabbit[(RabbitMQ)]
  Rabbit -->|OrderTransitionedV1| Audit[Order audit inbox]
```

Development Orders ownership switched after 64 historical SQL orders were copied and verified in PostgreSQL. The monolith rejects direct order create/read calls in extracted mode; its public payment route forwards payment intents to Orders. Catalog and Inventory remain extracted. RabbitMQ and Docker were unavailable during local validation, so the RabbitMQ arrows show configured paths; the successful through-gateway workflow used in-memory MassTransit. See [Order extraction](orders-extraction.md) and [cutover procedure](../migration/orders-cutover.md).
